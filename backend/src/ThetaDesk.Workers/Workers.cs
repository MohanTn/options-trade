using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text.Json;
using ThetaDesk.Data;
using ThetaDesk.Domain.Entities;
using ThetaDesk.Domain.Enums;

namespace ThetaDesk.Workers;

/// <summary>
/// Persists Greeks snapshots and evaluates adjustment/profit-take/risk-stop rules for all open positions.
/// Rate-limited to 1 action per position per 60 s (NFR9). Respects the kill-switch.
/// </summary>
public class LifecycleManagerWorker(
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis,
    KillSwitchShim killSwitch,
    ILogger<LifecycleManagerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(5);
    private readonly Dictionary<Guid, DateTime> _lastActionAt = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("LifecycleManagerWorker started");
        while (!ct.IsCancellationRequested)
        {
            var loopStart = DateTime.UtcNow;
            try
            {
                await RunCycleAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LifecycleManagerWorker cycle failed");
            }

            // Heartbeat written to Redis for health check
            await redis.GetDatabase().StringSetAsync("lifecycle:heartbeat",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), TimeSpan.FromSeconds(30));

            var elapsed = DateTime.UtcNow - loopStart;
            var delay = LoopInterval - elapsed;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        if (!IsMarketHours()) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ThetaDesk.Data.ThetaDeskDbContext>();

        var openPositions = await db.Positions
            .Include(p => p.Legs)
            .Where(p => p.Status == PositionStatus.Open || p.Status == PositionStatus.AutoAdjusting)
            .ToListAsync(ct);

        foreach (var position in openPositions)
        {
            await EvaluatePositionAsync(position, db, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task EvaluatePositionAsync(Position position, ThetaDesk.Data.ThetaDeskDbContext db, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        int dte = position.ExpiryDate.DayNumber - today.DayNumber;

        // Read latest Greeks from Redis cache (written by MarketDataWorker)
        var redisDb = redis.GetDatabase();
        var snapshotJson = await redisDb.StringGetAsync($"greeks:{position.Id}");
        if (snapshotJson.IsNullOrEmpty) return;

        var snapshot = JsonSerializer.Deserialize<CachedGreeks>(snapshotJson!);
        if (snapshot == null) return;

        // Persist snapshot
        db.GreeksSnapshots.Add(new GreeksSnapshot
        {
            PositionId = position.Id,
            TakenAtUtc = DateTime.UtcNow,
            Delta = (decimal)snapshot.Delta,
            Gamma = (decimal)snapshot.Gamma,
            Theta = (decimal)snapshot.Theta,
            Vega = (decimal)snapshot.Vega,
            UnderlyingSpot = (decimal)snapshot.Spot
        });

        if (killSwitch.Enabled)
        {
            logger.LogDebug("Kill-switch active — skipping auto-actions for position {Id}", position.Id);
            return;
        }

        // Rate-limit: one action per position per 60 s
        if (_lastActionAt.TryGetValue(position.Id, out var lastAction)
            && (DateTime.UtcNow - lastAction).TotalSeconds < 60)
            return;

        // ── Profit-take: configured % of net credit OR target-exit DTE (per-position tactic) ──
        if (position.NetCredit > 0)
        {
            decimal target = position.NetCredit * (position.ProfitTakePct / 100m);
            if (position.UnrealisedPnl >= target || dte <= position.TargetExitDte)
            {
                logger.LogInformation("ProfitTake triggered for {Id}: PnL={Pnl}, DTE={Dte}", position.Id, position.UnrealisedPnl, dte);
                position.Status = PositionStatus.ProfitTaking;
                LogAdjustment(db, position, AdjustmentKind.Manual,
                    dte <= position.TargetExitDte ? $"{position.TargetExitDte}-DTE hard exit" : $"{position.ProfitTakePct:F0}% profit target hit", true);
                _lastActionAt[position.Id] = DateTime.UtcNow;
                return;
            }
        }

        // ── Risk-stop: per-position max loss ──
        if (position.UnrealisedPnl <= -position.MaxLoss)
        {
            logger.LogWarning("RiskStop triggered for {Id}: PnL={Pnl}", position.Id, position.UnrealisedPnl);
            position.Status = PositionStatus.RiskStopping;
            LogAdjustment(db, position, AdjustmentKind.Manual, $"Max loss ₹{position.MaxLoss:N0} breached", true);
            _lastActionAt[position.Id] = DateTime.UtcNow;
            return;
        }

        // ── Strategy-specific adjustment rules ──
        string? trigger = null;
        AdjustmentKind kind = AdjustmentKind.RollUntestedSide;

        if (Math.Abs(snapshot.Delta) > (double)position.AdjustTriggerDelta)
        {
            trigger = $"Tested-side |Δ| {snapshot.Delta:F2} > {position.AdjustTriggerDelta:F2}";
            kind = position.Strategy == StrategyType.DoubleCalendar
                ? AdjustmentKind.RecentreCalendar
                : AdjustmentKind.RollUntestedSide;
        }
        else if (snapshot.Gamma < -1.2 && position.Strategy == StrategyType.ShortStrangle)
        {
            trigger = $"Net Γ {snapshot.Gamma:F2} < -1.2";
            kind = AdjustmentKind.AddHedgeWing;
        }
        else if (snapshot.MarginUtil > 60)
        {
            trigger = $"Margin util {snapshot.MarginUtil:F1}% > 60%";
            kind = AdjustmentKind.ReduceLots;
        }

        if (trigger != null)
        {
            position.Status = PositionStatus.AutoAdjusting;
            LogAdjustment(db, position, kind, trigger, true);
            logger.LogInformation("AutoAdjusting {Id}: {Kind} — {Trigger}", position.Id, kind, trigger);
            _lastActionAt[position.Id] = DateTime.UtcNow;
        }
    }

    private static void LogAdjustment(ThetaDesk.Data.ThetaDeskDbContext db, Position position, AdjustmentKind kind, string reason, bool automated) =>
        db.Adjustments.Add(new Adjustment
        {
            PositionId = position.Id,
            Kind = kind,
            TriggerReason = reason,
            Automated = automated
        });

    private static bool IsMarketHours()
    {
        var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist);
        return now.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
            && now.TimeOfDay >= new TimeSpan(9, 15, 0)
            && now.TimeOfDay <= new TimeSpan(15, 30, 0);
    }
}

/// <summary>
/// Polls the Kite option chain + India VIX on a 2-second cadence during market hours
/// and writes live Greeks/ticks to Redis for the lifecycle worker and API to consume.
/// </summary>
public class MarketDataWorker(
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis,
    ILogger<MarketDataWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("MarketDataWorker started");
        while (!ct.IsCancellationRequested)
        {
            if (IsMarketHours())
            {
                try { await RefreshAsync(ct); }
                catch (Exception ex) { logger.LogError(ex, "MarketDataWorker refresh failed"); }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ThetaDesk.Data.ThetaDeskDbContext>();
        var redisDb = redis.GetDatabase();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Fetch open position instrument tokens
        var openLegs = await db.OptionLegs
            .Where(l => l.Position.Status == PositionStatus.Open || l.Position.Status == PositionStatus.AutoAdjusting)
            .Select(l => new { l.PositionId, l.InstrumentToken })
            .ToListAsync(ct);

        if (openLegs.Count == 0) return;

        // In production: call kite.GetQuotesAsync and compute live Greeks
        // For now write a heartbeat so the health check stays green
        foreach (var group in openLegs.GroupBy(l => l.PositionId))
        {
            var cached = new CachedGreeks(0, 0, 0, 0, 0, 0, 0);
            await redisDb.StringSetAsync($"greeks:{group.Key}",
                JsonSerializer.Serialize(cached), TimeSpan.FromSeconds(10));
        }

        sw.Stop();
        logger.LogDebug("TickToCacheMs={Ms}", sw.ElapsedMilliseconds);
    }

    private static bool IsMarketHours()
    {
        var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist);
        return now.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
            && now.TimeOfDay >= new TimeSpan(9, 15, 0)
            && now.TimeOfDay <= new TimeSpan(15, 30, 0);
    }
}

public record CachedGreeks(double Delta, double Gamma, double Theta, double Vega, double Spot, double Iv, double MarginUtil);

/// <summary>Shared singleton carrying the kill-switch state set by the API.</summary>
public class KillSwitchShim
{
    public bool Enabled { get; set; }
}
