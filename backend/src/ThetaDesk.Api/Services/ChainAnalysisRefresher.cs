using Microsoft.EntityFrameworkCore;
using ThetaDesk.Api.Kite;
using ThetaDesk.Data;
using ThetaDesk.Domain.Entities;
using ThetaDesk.Domain.Enums;

namespace ThetaDesk.Api.Services;

/// <summary>
/// Re-runs the chain analysis every minute during market hours so the market read (bias, OI
/// order-flow, IV term structure) tracks conditions continuously — the UI and SignalEngine then
/// always find a fresh cache. Raises a Warning alert when the composed bias regime shifts,
/// rate-limited to one alert per 15 minutes so a score flapping around a label boundary
/// cannot flood the alert feed.
/// </summary>
public class ChainAnalysisRefresher(IServiceScopeFactory scopeFactory, IKiteClient kite, ILogger<ChainAnalysisRefresher> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Chain analysis refresher started ({Seconds}s cadence, market hours only)", Interval.TotalSeconds);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chain analysis refresh failed — will retry next tick");
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Outside market hours quotes are frozen and OI deltas meaningless; without a broker
        // session every scan would just fail. Skip quietly in both cases.
        if (!IsMarketHours() || !(await kite.GetSessionStatusAsync()).Valid) return;

        using var scope = scopeFactory.CreateScope();
        var analysis = scope.ServiceProvider.GetRequiredService<ChainAnalysisService>();

        var prev = await analysis.GetCachedAsync();
        var fresh = await analysis.AnalyzeAsync(force: true, ct);

        if (prev != null && prev.BiasLabel != fresh.BiasLabel)
            await RaiseBiasShiftAlertAsync(scope.ServiceProvider, prev, fresh, ct);
    }

    private async Task RaiseBiasShiftAlertAsync(IServiceProvider services, ChainAnalysis prev, ChainAnalysis fresh, CancellationToken ct)
    {
        var db = services.GetRequiredService<ThetaDeskDbContext>();

        var cutoff = DateTime.UtcNow - AlertCooldown;
        if (await db.Alerts.AnyAsync(a => a.Kind == "MarketBiasShift" && a.RaisedAtUtc > cutoff, ct))
            return;

        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        if (fund == null) return;

        db.Alerts.Add(new Alert
        {
            FundId = fund.Id,
            Kind = "MarketBiasShift",
            Severity = AlertSeverity.Warning,
            Message = $"Chain bias shifted: {prev.BiasLabel} ({prev.BiasScore:+0.00;-0.00}) → {fresh.BiasLabel} ({fresh.BiasScore:+0.00;-0.00}). "
                    + $"Spot {fresh.Spot:#,0}, weekly ATM IV {fresh.NearWeek.AtmIv:P1}, PCR {fresh.NearWeek.PcrOi:F2}.",
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Market bias shifted: {Prev} → {Now} (score {Score:+0.00;-0.00})",
            prev.BiasLabel, fresh.BiasLabel, fresh.BiasScore);
    }

    // NSE F&O hours with a small buffer either side: 09:10–15:35 IST, Mon–Fri.
    private static bool IsMarketHours()
    {
        var ist = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        if (ist.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var hm = ist.Hour * 100 + ist.Minute;
        return hm is >= 910 and <= 1535;
    }
}
