using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using ThetaDesk.Api.Kite;
using ThetaDesk.Data;
using ThetaDesk.Domain.Entities;
using ThetaDesk.Greeks;

namespace ThetaDesk.Api.Services;

/// <summary>One strike of the analysed chain. OI changes are vs the first observation of the IST day.</summary>
public record ChainStrikeRow(
    decimal Strike,
    long CeOi, long PeOi,
    long CeOiChange, long PeOiChange,
    long CeVolume, long PeVolume,
    decimal CeLtp, decimal PeLtp,
    decimal CeIv, decimal PeIv,
    decimal CeVega, decimal PeVega); // ₹ per 1% IV move per unit, from Black-76 at the solved IV

/// <summary>Aggregated read of one expiry's chain around the money.</summary>
public record ExpiryAnalysis(
    DateOnly Expiry, int Dte, bool IsMonthly,
    decimal AtmIv,              // fraction, e.g. 0.14
    decimal PcrOi, decimal PcrVolume,
    decimal MaxPain,            // strike where option writers' payout is minimal
    decimal SupportStrike,      // max PE OI — where put writers defend
    decimal ResistanceStrike,   // max CE OI — where call writers cap
    long TotalCeOi, long TotalPeOi,
    long CeOiChange, long PeOiChange,
    decimal SkewPct,            // OTM-put IV − OTM-call IV, percentage points
    decimal AtmStraddle,        // ATM CE + PE premium = the market's priced move to expiry
    decimal ExpectedMovePct,    // AtmStraddle as % of spot
    decimal StraddleChangePct,  // straddle vs first observation today (falling = range-bound day)
    decimal SupportShift,       // points the max-PE-OI wall moved since day open (+ = climbing)
    decimal ResistanceShift,    // points the max-CE-OI wall moved since day open
    // Premium-flow (leading indicator): Σ OTM option prices, basket anchored to the MORNING spot
    // so membership never shifts intraday. Theta decays both sides equally, so the *divergence*
    // of the two changes is the directional read: calls gaining on puts = bullish and vice versa.
    decimal CePremiumSum,       // Σ CE LTP for strikes above the morning spot
    decimal PePremiumSum,       // Σ PE LTP for strikes below the morning spot
    decimal CePremiumChangePct, // CE basket vs its morning capture
    decimal PePremiumChangePct, // PE basket vs its morning capture
    // Vega flow — the cleaner leading read (per the Vibhore Gupta method): premium sums carry the
    // full delta component when spot moves, while per-side Σ vega mostly reflects volatility
    // demand. A side's vega rising = that side being bought; both decaying = non-directional day.
    decimal CeVegaSum,
    decimal PeVegaSum,
    decimal CeVegaChangePct,
    decimal PeVegaChangePct,
    IReadOnlyList<ChainStrikeRow> Strikes);

/// <summary>One intraday sample of the per-side Σ-vega change vs morning, for the flow chart.</summary>
public record VegaFlowPoint(DateTime AtUtc, decimal WeekCe, decimal WeekPe, decimal MonthCe, decimal MonthPe, decimal Spot);

/// <summary>
/// The full pre-prediction: both expiries plus a composed directional bias for the option seller.
/// BiasScore ∈ [−1, +1] (+ = bullish drift expected); Drivers explain each contribution.
/// </summary>
public record ChainAnalysis(
    DateTime GeneratedAtUtc,
    decimal Spot, decimal Vix,
    decimal MorningVix,         // first VIX print seen today — intraday trend gates selling aggression
    ExpiryAnalysis NearWeek, ExpiryAnalysis NearMonth,
    decimal TermSpreadPct,      // weekly ATM IV − monthly ATM IV, percentage points
    string TermStructure,
    decimal BiasScore,
    string BiasLabel,
    IReadOnlyList<string> Drivers,
    string SellerPlaybook);

/// <summary>
/// Scans the NIFTY option chain for the near-week and near-month expiries and turns raw
/// OI / volume / price data into a seller-oriented market read: put-call ratios, max pain,
/// OI-based support/resistance, IV skew and term structure, and intraday OI order-flow
/// (writing pressure) measured against a per-day Redis baseline. The composed bias feeds the
/// SignalEngine's strike skew and the operator's Option Chain view.
/// </summary>
public class ChainAnalysisService(IKiteClient kite, IConnectionMultiplexer redis, ThetaDeskDbContext dbContext, ILogger<ChainAnalysisService> logger)
{
    private const string CacheKey = "chain:analysis:latest";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(3);
    private const double RiskFreeRate = 0.065;
    private const int StrikesPerSide = 12;          // strikes analysed each side of spot, per expiry
    private const decimal SkewMoneyness = 0.02m;    // OTM distance (2% of spot) used for the skew read
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private const int MarketCloseHm = 1530;         // 15:30 IST — the day's vega-flow snapshot is archived from this point on

    /// <summary>Returns the cached analysis if one is fresh, else null. Never calls the broker.</summary>
    public async Task<ChainAnalysis?> GetCachedAsync()
    {
        var raw = await redis.GetDatabase().StringGetAsync(CacheKey);
        if (raw.IsNullOrEmpty) return null;
        try
        {
            return JsonSerializer.Deserialize<ChainAnalysis>(raw!);
        }
        catch (JsonException)
        {
            // Stale shape from a previous deploy — treat as "no analysis" rather than failing callers.
            await redis.GetDatabase().KeyDeleteAsync(CacheKey);
            return null;
        }
    }

    /// <summary>Runs (or returns the cached) full chain analysis. Set <paramref name="force"/> to bypass the cache.</summary>
    public async Task<ChainAnalysis> AnalyzeAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && await GetCachedAsync() is { } cached)
            return cached;

        var instruments = await kite.GetNiftyInstrumentsAsync(ct);
        var spot = await kite.GetNiftySpotAsync(ct);
        if (spot <= 0)
            throw new InvalidOperationException("NIFTY spot unavailable — cannot anchor the chain scan.");
        var vix = await kite.GetIndiaVixAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var expiries = instruments.Select(i => i.Expiry).Distinct().Where(e => e >= today).OrderBy(e => e).ToList();
        if (expiries.Count < 2)
            throw new InvalidOperationException("Not enough NIFTY expiries available to analyse the chain.");

        // Monthly expiries are the last expiry within each calendar month.
        var monthlies = expiries.GroupBy(e => (e.Year, e.Month)).Select(g => g.Max()).ToHashSet();
        var nearWeek = expiries[0];
        var monthliesAhead = expiries.Where(e => monthlies.Contains(e) && e > nearWeek).ToList();
        var nearMonth = monthliesAhead.FirstOrDefault();
        // A monthly inside 10 DTE trades like a weekly — track the following monthly instead.
        if (nearMonth != default && nearMonth.DayNumber - today.DayNumber < 10 && monthliesAhead.Count > 1)
            nearMonth = monthliesAhead[1];
        if (nearMonth == default)
            nearMonth = expiries[^1]; // degenerate chain — fall back to the furthest expiry

        // One quote round-trip for both expiries' near-the-money strikes.
        var weekInsts = NearTheMoney(instruments, nearWeek, spot);
        var monthInsts = NearTheMoney(instruments, nearMonth, spot);
        var quotes = (await kite.GetQuotesAsync(weekInsts.Concat(monthInsts).Select(i => i.Token), ct))
            .ToDictionary(q => q.Token);

        var baseline = await LoadOiBaselineAsync(quotes);
        var morningVix = await MorningVixAsync(vix);

        var week = await WithDayShiftsAsync(AnalyzeExpiry(nearWeek, today, isMonthly: monthlies.Contains(nearWeek), weekInsts, quotes, baseline, spot), spot);
        var month = await WithDayShiftsAsync(AnalyzeExpiry(nearMonth, today, isMonthly: monthlies.Contains(nearMonth), monthInsts, quotes, baseline, spot), spot);

        var result = Compose(spot, vix, morningVix, week, month);
        await redis.GetDatabase().StringSetAsync(CacheKey, JsonSerializer.Serialize(result), CacheTtl);
        await AppendVegaFlowAsync(week, month, spot);
        await SnapshotDailyVegaFlowIfDueAsync(ct);
        logger.LogInformation("Chain analysis: spot {Spot}, bias {Bias} ({Score:+0.00;-0.00}), weekly ATM IV {WeekIv:P1} vs monthly {MonthIv:P1}",
            spot, result.BiasLabel, result.BiasScore, week.AtmIv, month.AtmIv);
        return result;
    }

    private static string VegaFlowKey() =>
        $"chain:vegaflow:{TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist):yyyyMMdd}";

    /// <summary>
    /// Appends the current vega-flow reading to today's series (samples ≥45s apart). Only samples
    /// during market hours — an off-hours forced rescan would otherwise stretch the chart's time
    /// axis with flat overnight points.
    /// </summary>
    private async Task AppendVegaFlowAsync(ExpiryAnalysis week, ExpiryAnalysis month, decimal spot)
    {
        var ist = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        if (ist.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        // One minute wider than the refresher's 09:10–15:35 window: a tick that starts on the
        // boundary reaches this point only after its broker round-trips.
        var hm = ist.Hour * 100 + ist.Minute;
        if (hm is < 909 or > 1536) return;

        var db = redis.GetDatabase();
        var key = VegaFlowKey();
        var last = await db.ListGetByIndexAsync(key, -1);
        if (!last.IsNullOrEmpty)
        {
            try
            {
                if (JsonSerializer.Deserialize<VegaFlowPoint>(last!) is { } prev &&
                    DateTime.UtcNow - prev.AtUtc < TimeSpan.FromSeconds(45))
                    return; // a forced refresh right after a scheduled one shouldn't double-sample
            }
            catch (JsonException) { /* stale shape — fall through and append */ }
        }
        await db.ListRightPushAsync(key, JsonSerializer.Serialize(new VegaFlowPoint(
            DateTime.UtcNow, week.CeVegaChangePct, week.PeVegaChangePct, month.CeVegaChangePct, month.PeVegaChangePct, spot)));
        await db.KeyExpireAsync(key, TimeSpan.FromHours(30));
    }

    /// <summary>Today's intraday vega-flow series, oldest first. Never calls the broker.</summary>
    public async Task<IReadOnlyList<VegaFlowPoint>> GetVegaFlowSeriesAsync()
    {
        var raw = await redis.GetDatabase().ListRangeAsync(VegaFlowKey());
        var points = new List<VegaFlowPoint>(raw.Length);
        foreach (var v in raw)
        {
            try
            {
                if (JsonSerializer.Deserialize<VegaFlowPoint>(v!) is { } p) points.Add(p);
            }
            catch (JsonException) { /* skip points written by an older build */ }
        }
        return points;
    }

    /// <summary>
    /// From market close (15:30 IST) on, archives today's full Redis vega-flow series into
    /// Postgres as a permanent snapshot — at most one row per trading day (the unique index on
    /// TradingDate makes this idempotent even if called repeatedly by the 60s refresher). Unlike
    /// the Redis series, this never expires and is never overwritten; the operator deletes rows
    /// manually when they no longer want them.
    /// </summary>
    private async Task SnapshotDailyVegaFlowIfDueAsync(CancellationToken ct)
    {
        var ist = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        if (ist.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        if (ist.Hour * 100 + ist.Minute < MarketCloseHm) return;

        var tradingDate = DateOnly.FromDateTime(ist);
        if (await dbContext.VegaFlowDailySnapshots.AnyAsync(s => s.TradingDate == tradingDate, ct)) return;

        var points = await GetVegaFlowSeriesAsync();
        if (points.Count == 0) return; // nothing sampled today (e.g. no session) — leave the day unarchived

        dbContext.VegaFlowDailySnapshots.Add(new VegaFlowDailySnapshot
        {
            TradingDate = tradingDate,
            PointsJson = JsonSerializer.Serialize(points),
        });
        try
        {
            await dbContext.SaveChangesAsync(ct);
            logger.LogInformation("Archived {Count} vega-flow points for {Date} to Postgres", points.Count, tradingDate);
        }
        catch (DbUpdateException ex)
        {
            // Most likely cause: a concurrent call (manual rescan racing the 60s refresher, both
            // inside the 15:30–15:35 window) already inserted today's row and the unique index on
            // TradingDate caught it — the day is archived either way. This is a background side
            // effect, not the caller's problem: log and swallow so AnalyzeAsync still returns the
            // analysis it already computed instead of failing the whole request over it.
            logger.LogWarning(ex, "Vega-flow snapshot insert for {Date} failed (likely a concurrent duplicate) — ignoring", tradingDate);
        }
    }

    /// <summary>Trading dates with a permanent EOD vega-flow snapshot, most recent first.</summary>
    public async Task<IReadOnlyList<DateOnly>> GetVegaFlowSnapshotDatesAsync(CancellationToken ct = default) =>
        await dbContext.VegaFlowDailySnapshots.OrderByDescending(s => s.TradingDate).Select(s => s.TradingDate).ToListAsync(ct);

    /// <summary>One trading day's archived vega-flow series, or null if that day has no snapshot (not yet market close, or never captured).</summary>
    public async Task<IReadOnlyList<VegaFlowPoint>?> GetVegaFlowSnapshotAsync(DateOnly date, CancellationToken ct = default)
    {
        var snapshot = await dbContext.VegaFlowDailySnapshots.FirstOrDefaultAsync(s => s.TradingDate == date, ct);
        if (snapshot == null) return null;
        try
        {
            return JsonSerializer.Deserialize<List<VegaFlowPoint>>(snapshot.PointsJson) ?? [];
        }
        catch (JsonException)
        {
            logger.LogWarning("Vega-flow snapshot for {Date} has an unreadable PointsJson payload", date);
            return [];
        }
    }

    /// <summary>First VIX print seen today (IST), seeded on first call — the intraday-trend anchor.</summary>
    private async Task<decimal> MorningVixAsync(decimal vix)
    {
        var db = redis.GetDatabase();
        var key = $"chain:daybase:{TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist):yyyyMMdd}";
        var raw = await db.HashGetAsync(key, "_vix"); // "_vix" can't collide with yyyy-MM-dd expiry fields
        if (!raw.IsNullOrEmpty &&
            decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var stored))
            return stored;

        await db.HashSetAsync(key, "_vix", vix.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await db.KeyExpireAsync(key, TimeSpan.FromHours(30));
        return vix;
    }

    private static List<KiteInstrument> NearTheMoney(IReadOnlyList<KiteInstrument> instruments, DateOnly expiry, decimal spot)
    {
        var strikes = instruments.Where(i => i.Expiry == expiry).Select(i => i.Strike).Distinct()
            .OrderBy(s => Math.Abs(s - spot)).Take(StrikesPerSide * 2 + 1).ToHashSet();
        return instruments.Where(i => i.Expiry == expiry && strikes.Contains(i.Strike)).ToList();
    }

    /// <summary>
    /// OI baseline = the first OI seen for each token today (IST). OI changes are measured against it,
    /// so they read "since ThetaDesk first looked today", not since exchange open. Stored as a Redis
    /// hash per day; new tokens are appended, existing entries are never overwritten.
    /// </summary>
    private async Task<Dictionary<long, long>> LoadOiBaselineAsync(Dictionary<long, KiteQuote> quotes)
    {
        var ist = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        var key = $"chain:oibase:{ist:yyyyMMdd}";
        var db = redis.GetDatabase();

        var existing = (await db.HashGetAllAsync(key))
            .ToDictionary(e => (long)e.Name, e => (long)e.Value);

        var missing = quotes.Values.Where(q => !existing.ContainsKey(q.Token))
            .Select(q => new HashEntry(q.Token, q.Oi)).ToArray();
        if (missing.Length > 0)
        {
            await db.HashSetAsync(key, missing);
            await db.KeyExpireAsync(key, TimeSpan.FromHours(30)); // survives the trading day, gone tomorrow
            foreach (var e in missing) existing[(long)e.Name] = (long)e.Value;
        }
        return existing;
    }

    private static ExpiryAnalysis AnalyzeExpiry(
        DateOnly expiry, DateOnly today, bool isMonthly,
        List<KiteInstrument> insts, Dictionary<long, KiteQuote> quotes, Dictionary<long, long> baseline, decimal spot)
    {
        double t = Math.Max(expiry.DayNumber - today.DayNumber, 1) / 365.0;

        var rows = insts
            .Where(i => quotes.ContainsKey(i.Token))
            .GroupBy(i => i.Strike)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ce = g.FirstOrDefault(i => i.OptionType == "CE");
                var pe = g.FirstOrDefault(i => i.OptionType == "PE");
                var ceQ = ce != null ? quotes[ce.Token] : null;
                var peQ = pe != null ? quotes[pe.Token] : null;
                decimal ceIv = SolveIv(ceQ, g.Key, spot, t, isCall: true);
                decimal peIv = SolveIv(peQ, g.Key, spot, t, isCall: false);
                return new ChainStrikeRow(
                    g.Key,
                    ceQ?.Oi ?? 0, peQ?.Oi ?? 0,
                    ceQ != null ? ceQ.Oi - baseline.GetValueOrDefault(ceQ.Token, ceQ.Oi) : 0,
                    peQ != null ? peQ.Oi - baseline.GetValueOrDefault(peQ.Token, peQ.Oi) : 0,
                    ceQ?.Volume ?? 0, peQ?.Volume ?? 0,
                    ceQ?.Ltp ?? 0, peQ?.Ltp ?? 0,
                    ceIv, peIv,
                    ceIv > 0 ? (decimal)Black76.ComputeCall((double)spot, (double)g.Key, t, RiskFreeRate, (double)ceIv).Vega : 0,
                    peIv > 0 ? (decimal)Black76.ComputePut((double)spot, (double)g.Key, t, RiskFreeRate, (double)peIv).Vega : 0);
            })
            .ToList();
        if (rows.Count == 0)
            throw new InvalidOperationException(
                $"No live quotes for the {expiry:dd-MMM-yy} chain — quotes may be unavailable outside market hours.");

        long totalCeOi = rows.Sum(r => r.CeOi), totalPeOi = rows.Sum(r => r.PeOi);
        long ceVol = rows.Sum(r => r.CeVolume), peVol = rows.Sum(r => r.PeVolume);

        var atmRow = rows.OrderBy(r => Math.Abs(r.Strike - spot)).First();
        // ATM IV = mean of the sides that produced a solvable IV.
        var atmIvs = new[] { atmRow.CeIv, atmRow.PeIv }.Where(iv => iv > 0).ToList();
        decimal atmIv = atmIvs.Count > 0 ? atmIvs.Average() : 0;

        var putSide = rows.OrderBy(r => Math.Abs(r.Strike - spot * (1 - SkewMoneyness))).First();
        var callSide = rows.OrderBy(r => Math.Abs(r.Strike - spot * (1 + SkewMoneyness))).First();
        decimal skewPct = putSide.PeIv > 0 && callSide.CeIv > 0 ? (putSide.PeIv - callSide.CeIv) * 100 : 0;

        // ATM straddle = the move the market has priced in to this expiry.
        decimal atmStraddle = atmRow.CeLtp + atmRow.PeLtp;

        return new ExpiryAnalysis(
            expiry, expiry.DayNumber - today.DayNumber, isMonthly,
            atmIv,
            PcrOi: Ratio(totalPeOi, totalCeOi),
            PcrVolume: Ratio(peVol, ceVol),
            MaxPain: MaxPain(rows),
            SupportStrike: rows.MaxBy(r => r.PeOi)!.Strike,
            ResistanceStrike: rows.MaxBy(r => r.CeOi)!.Strike,
            totalCeOi, totalPeOi,
            rows.Sum(r => r.CeOiChange), rows.Sum(r => r.PeOiChange),
            skewPct,
            atmStraddle,
            ExpectedMovePct: Math.Round(atmStraddle / spot * 100, 2),
            // Intraday-drift fields are all filled by WithDayShiftsAsync.
            StraddleChangePct: 0, SupportShift: 0, ResistanceShift: 0,
            CePremiumSum: 0, PePremiumSum: 0, CePremiumChangePct: 0, PePremiumChangePct: 0,
            CeVegaSum: 0, PeVegaSum: 0, CeVegaChangePct: 0, PeVegaChangePct: 0,
            rows);
    }

    // Morning capture for one expiry. CeBase/PeBase hold per-strike OTM prices (key = invariant
    // strike string) so intraday comparisons always use the identical strike set on both sides,
    // even when the analysed window drifts with spot.
    private sealed record DayBase(decimal Straddle, decimal Support, decimal Resistance, decimal Spot = 0)
    {
        public Dictionary<string, decimal> CeBase { get; init; } = [];
        public Dictionary<string, decimal> PeBase { get; init; } = [];
        public Dictionary<string, decimal> CeVegaBase { get; init; } = [];
        public Dictionary<string, decimal> PeVegaBase { get; init; } = [];
    }

    private static string StrikeKey(decimal strike) =>
        strike.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<string, decimal> CaptureBasket(
        IReadOnlyList<ChainStrikeRow> rows, decimal refSpot, bool callSide, Func<ChainStrikeRow, decimal> value) =>
        rows.Where(r => (callSide ? r.Strike > refSpot : r.Strike < refSpot) && value(r) > 0)
            .ToDictionary(r => StrikeKey(r.Strike), value);

    // Sums current and morning values over the SAME strikes (intersection), so window drift or a
    // strike losing its quote can never masquerade as flow.
    private static (decimal Current, decimal ChangePct) BasketDrift(
        IReadOnlyList<ChainStrikeRow> rows, Dictionary<string, decimal> basket, decimal refSpot, bool callSide,
        Func<ChainStrikeRow, decimal> value)
    {
        decimal baseSum = 0, curSum = 0;
        foreach (var r in rows)
        {
            var v = value(r);
            if (v <= 0 || (callSide ? r.Strike <= refSpot : r.Strike >= refSpot)) continue;
            if (basket.TryGetValue(StrikeKey(r.Strike), out var morning))
            {
                baseSum += morning;
                curSum += v;
            }
        }
        return (Math.Round(curSum, 2), baseSum > 0 ? Math.Round((curSum - baseSum) / baseSum * 100, 1) : 0);
    }

    /// <summary>
    /// Fills the intraday-drift fields by comparing against the first values seen today (IST):
    /// straddle premium drift, OI wall migration, and the OTM premium-flow baskets. The first
    /// observation of the day seeds the baseline, including the reference spot that anchors
    /// basket membership for the rest of the day.
    /// </summary>
    private async Task<ExpiryAnalysis> WithDayShiftsAsync(ExpiryAnalysis e, decimal spot)
    {
        var db = redis.GetDatabase();
        var key = $"chain:daybase:{TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist):yyyyMMdd}";
        var field = e.Expiry.ToString("yyyy-MM-dd");

        DayBase? dayBase = null;
        var raw = await db.HashGetAsync(key, field);
        if (!raw.IsNullOrEmpty)
        {
            try { dayBase = JsonSerializer.Deserialize<DayBase>(raw!); }
            catch (JsonException) { /* stale shape — reseed below */ }
        }

        // Baskets are anchored to the day's reference spot (live spot only when seeding).
        decimal refSpot = dayBase?.Spot > 0 ? dayBase.Spot : spot;

        if (dayBase == null)
        {
            dayBase = new DayBase(e.AtmStraddle, e.SupportStrike, e.ResistanceStrike, refSpot)
            {
                CeBase = CaptureBasket(e.Strikes, refSpot, callSide: true, r => r.CeLtp),
                PeBase = CaptureBasket(e.Strikes, refSpot, callSide: false, r => r.PeLtp),
                CeVegaBase = CaptureBasket(e.Strikes, refSpot, callSide: true, r => r.CeVega),
                PeVegaBase = CaptureBasket(e.Strikes, refSpot, callSide: false, r => r.PeVega),
            };
            await db.HashSetAsync(key, field, JsonSerializer.Serialize(dayBase));
            await db.KeyExpireAsync(key, TimeSpan.FromHours(30));
        }
        else
        {
            // Baselines written by an older build may be missing some baskets — patch only the
            // missing ones without disturbing anchors captured earlier today.
            bool patched = false;
            if (dayBase.CeBase.Count == 0 && dayBase.PeBase.Count == 0)
            {
                dayBase = dayBase with
                {
                    Spot = refSpot,
                    CeBase = CaptureBasket(e.Strikes, refSpot, callSide: true, r => r.CeLtp),
                    PeBase = CaptureBasket(e.Strikes, refSpot, callSide: false, r => r.PeLtp),
                };
                patched = true;
            }
            if (dayBase.CeVegaBase.Count == 0 && dayBase.PeVegaBase.Count == 0)
            {
                dayBase = dayBase with
                {
                    CeVegaBase = CaptureBasket(e.Strikes, refSpot, callSide: true, r => r.CeVega),
                    PeVegaBase = CaptureBasket(e.Strikes, refSpot, callSide: false, r => r.PeVega),
                };
                patched = true;
            }
            if (patched)
                await db.HashSetAsync(key, field, JsonSerializer.Serialize(dayBase));
        }

        var (ceSum, ceChange) = BasketDrift(e.Strikes, dayBase.CeBase, refSpot, callSide: true, r => r.CeLtp);
        var (peSum, peChange) = BasketDrift(e.Strikes, dayBase.PeBase, refSpot, callSide: false, r => r.PeLtp);
        var (ceVegaSum, ceVegaChange) = BasketDrift(e.Strikes, dayBase.CeVegaBase, refSpot, callSide: true, r => r.CeVega);
        var (peVegaSum, peVegaChange) = BasketDrift(e.Strikes, dayBase.PeVegaBase, refSpot, callSide: false, r => r.PeVega);

        return e with
        {
            StraddleChangePct = dayBase.Straddle > 0 ? Math.Round((e.AtmStraddle - dayBase.Straddle) / dayBase.Straddle * 100, 1) : 0,
            SupportShift = e.SupportStrike - dayBase.Support,
            ResistanceShift = e.ResistanceStrike - dayBase.Resistance,
            CePremiumSum = ceSum,
            PePremiumSum = peSum,
            CePremiumChangePct = ceChange,
            PePremiumChangePct = peChange,
            CeVegaSum = ceVegaSum,
            PeVegaSum = peVegaSum,
            CeVegaChangePct = ceVegaChange,
            PeVegaChangePct = peVegaChange,
        };
    }

    private static decimal SolveIv(KiteQuote? q, decimal strike, decimal spot, double t, bool isCall)
    {
        if (q == null || q.Ltp <= 0) return 0;
        if (q.Iv > 0) return q.Iv / 100m; // broker-supplied IV (percent) wins when present
        return (decimal)Black76.SolveIv((double)q.Ltp, (double)spot, (double)strike, t, RiskFreeRate, isCall);
    }

    private static decimal Ratio(long num, long den) => den > 0 ? Math.Round((decimal)num / den, 2) : 0;

    /// <summary>Settlement level at which total option-writer payout (CE + PE intrinsic × OI) is minimal.</summary>
    private static decimal MaxPain(List<ChainStrikeRow> rows) =>
        rows.MinBy(candidate => rows.Sum(r =>
            r.CeOi * Math.Max(candidate.Strike - r.Strike, 0) +
            r.PeOi * Math.Max(r.Strike - candidate.Strike, 0)))!.Strike;

    private static ChainAnalysis Compose(decimal spot, decimal vix, decimal morningVix, ExpiryAnalysis week, ExpiryAnalysis month)
    {
        var drivers = new List<string>();

        // Vega flow — the primary leading read (Vibhore Gupta method). A side's Σ vega rising =
        // that side being bought; both decaying = writers in control, non-directional day. Its
        // spot-mechanical component (vega drifts with moneyness) is far smaller than the delta
        // baked into premium sums, and unlike OI (exchange-delayed ~3 min) it updates every tick.
        decimal vegaFlow = Blend(e => (e.CeVegaChangePct - e.PeVegaChangePct) / 15m);
        drivers.Add($"Vega flow (leading): weekly CE ν {week.CeVegaChangePct:+0.0;-0.0}% vs PE ν {week.PeVegaChangePct:+0.0;-0.0}% since morning — " +
                    (vegaFlow > 0.15m ? "call side being bid (bullish)"
                     : vegaFlow < -0.15m ? "put side being bid (bearish)"
                     : week.CeVegaChangePct < 0 && week.PeVegaChangePct < 0 ? "both sides decaying — non-directional, a premium-seller's day"
                     : "no clear vega signal"));

        // Premium flow — the raw-price version of the same idea. Confirms vega flow but includes
        // a mechanical delta component when spot moves, so it carries less weight.
        decimal premFlow = Blend(e => (e.CePremiumChangePct - e.PePremiumChangePct) / 15m);
        drivers.Add($"Premium flow: weekly OTM CE Σ ₹{week.CePremiumSum:#,0} ({week.CePremiumChangePct:+0.0;-0.0}%) vs PE Σ ₹{week.PePremiumSum:#,0} ({week.PePremiumChangePct:+0.0;-0.0}%) since morning — " +
                    (premFlow > 0.15m ? "call side repricing up (bullish pressure)" : premFlow < -0.15m ? "put side gaining / calls being sold down (bearish pressure)" : "both sides decaying in step"));

        // Each component ∈ [−1, +1], + = bullish. Near-week dominates: that's where writers commit
        // first. Each expiry's reading is clamped before blending so one extreme print cannot
        // silently drown out an opposing signal from the other expiry.
        decimal Blend(Func<ExpiryAnalysis, decimal> f) => Clamp(f(week)) * 0.6m + Clamp(f(month)) * 0.4m;

        // PCR (OI): heavy put writing (PCR > 1) builds a floor; heavy call writing caps upside.
        decimal pcr = Blend(e => (e.PcrOi - 1m) / 0.5m);
        drivers.Add($"PCR(OI) {week.PcrOi:F2} weekly / {month.PcrOi:F2} monthly — " +
                    (pcr > 0.15m ? "put writers dominate (supportive)" : pcr < -0.15m ? "call writers dominate (capped)" : "balanced open interest"));

        // OI order-flow: which side is *adding* contracts today = fresh writing conviction.
        decimal flow = Blend(e =>
        {
            decimal denom = Math.Abs(e.CeOiChange) + Math.Abs(e.PeOiChange);
            return denom > 0 ? (e.PeOiChange - e.CeOiChange) / denom : 0;
        });
        drivers.Add($"OI flow today: ΔPE {week.PeOiChange:+#,0;-#,0;0} / ΔCE {week.CeOiChange:+#,0;-#,0;0} (weekly) — " +
                    (flow > 0.15m ? "fresh put writing (bullish)" : flow < -0.15m ? "fresh call writing (bearish)" : "no clear side"));

        // Max pain gravity: expiries tend to drift toward the writers' least-pain strike.
        decimal pain = Blend(e => spot > 0 ? (e.MaxPain - spot) / spot * 100m : 0);
        drivers.Add($"Max pain {week.MaxPain:#,0} weekly vs spot {spot:#,0} — " +
                    (pain > 0.15m ? "pull upward" : pain < -0.15m ? "pull downward" : "pinned near spot"));

        // Skew: puts priced far over calls = downside hedging demand = bearish tilt.
        decimal skew = Blend(e => -e.SkewPct / 3m);
        drivers.Add($"IV skew (put−call) {week.SkewPct:+0.0;-0.0}pp weekly — " +
                    (skew < -0.15m ? "downside fear priced in" : skew > 0.15m ? "upside chase priced in" : "flat skew"));

        // Wall migration: writers repositioning intraday. Both walls climbing = bullish
        // repositioning, descending = bearish. Normalised so a ~0.4%-of-spot move per wall scores full.
        decimal walls = Blend(e => (e.SupportShift + e.ResistanceShift) / Math.Max(spot * 0.008m, 1m));
        drivers.Add($"OI wall shift today (weekly): support {week.SupportShift:+#,0;-#,0;±0} / resistance {week.ResistanceShift:+#,0;-#,0;±0} — " +
                    (walls > 0.15m ? "walls climbing (bullish repositioning)" : walls < -0.15m ? "walls descending (bearish repositioning)" : "walls static"));

        // Straddle drift is direction-neutral (it reads volatility, not side), so it informs the
        // playbook but not the bias score. Falling premium = range-bound day; rising = expansion.
        drivers.Add($"ATM straddle (weekly) ₹{week.AtmStraddle:#,0} = ±{week.ExpectedMovePct:F1}% priced move, {week.StraddleChangePct:+0.0;-0.0}% today — " +
                    (week.StraddleChangePct > 5 ? "vol expanding (danger for sellers)" : week.StraddleChangePct < -5 ? "premium decaying (range-bound, theta-friendly)" : "drifting normally"));

        decimal score = Math.Round(Clamp(vegaFlow * 0.25m + premFlow * 0.10m + pcr * 0.15m + flow * 0.15m + walls * 0.15m + pain * 0.10m + skew * 0.10m), 2);
        string label = score switch
        {
            > 0.45m => "Bullish",
            > 0.15m => "Mildly Bullish",
            < -0.45m => "Bearish",
            < -0.15m => "Mildly Bearish",
            _ => "Neutral / Range-bound",
        };

        decimal termSpreadPct = Math.Round((week.AtmIv - month.AtmIv) * 100, 2);
        string termStructure = termSpreadPct > 0.5m
            ? "Backwardation — weekly IV rich vs monthly"
            : termSpreadPct < -0.5m
                ? "Contango — monthly carries the premium"
                : "Flat term structure";

        // Intraday VIX trend gates aggression: selling into a rising VIX is fighting the tape.
        string vixTrend = morningVix > 0 && Math.Abs(vix - morningVix) / morningVix > 0.01m
            ? (vix < morningVix
                ? $"VIX easing from {morningVix:F1} at open to {vix:F1} — green light for aggressive writing. "
                : $"VIX rising from {morningVix:F1} at open to {vix:F1} — stay conservative, size writing down. ")
            : "";

        var playbook =
            (vix < 12m
                ? $"VIX {vix:F1} is low — premium is cheap; debit/buying structures have the edge today, size naked selling down. "
                : vix >= 18m
                    ? $"VIX {vix:F1} is high — premium is rich; a seller's day (keep risk defined). "
                    : $"VIX {vix:F1} mid-regime — normal theta-selling conditions. ")
            + vixTrend
            + (termSpreadPct > 0.5m
                ? $"Weekly premium is rich (+{termSpreadPct:F1}pp over monthly) — favour near-week credit structures. "
                : termSpreadPct < -0.5m
                    ? $"Monthly IV is {-termSpreadPct:F1}pp over weekly — monthly credit structures (or calendars selling the far leg) are better paid. "
                    : "Term structure is flat — pick expiry by DTE preference, not IV edge. ")
            + $"Weekly expected move is ±{week.ExpectedMovePct:F1}% (straddle ₹{week.AtmStraddle:#,0}): short strikes beyond {spot - week.AtmStraddle:#,0} / {spot + week.AtmStraddle:#,0} sit outside the priced range. "
            + $"OI walls: support {week.SupportStrike:#,0} / resistance {week.ResistanceStrike:#,0} (weekly) — short strikes beyond those walls ride the writers' defence. "
            + (week.StraddleChangePct > 5
                ? "Straddle is expanding today — volatility is being repriced; prefer defined-risk structures and tighter stops. "
                : week.StraddleChangePct < -5
                    ? "Straddle is decaying today — a range-bound session; theta harvest is working. "
                    : "")
            + (score > 0.15m
                ? "Bias is bullish: keep short puts nearer and push short calls wider."
                : score < -0.15m
                    ? "Bias is bearish: keep short calls nearer and push short puts wider."
                    : "No directional edge: stay delta-neutral and let theta work.");

        return new ChainAnalysis(DateTime.UtcNow, spot, vix, morningVix, week, month,
            termSpreadPct, termStructure, score, label, drivers, playbook);
    }

    private static decimal Clamp(decimal v) => Math.Clamp(v, -1m, 1m);
}
