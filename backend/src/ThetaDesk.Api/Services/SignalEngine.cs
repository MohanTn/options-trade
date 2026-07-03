using Microsoft.EntityFrameworkCore;
using ThetaDesk.Data;
using ThetaDesk.Api.Kite;
using ThetaDesk.Domain.Entities;
using ThetaDesk.Domain.Enums;
using ThetaDesk.Greeks;

namespace ThetaDesk.Api.Services;

public class SignalEngine(ThetaDeskDbContext db, IKiteClient kite, ChainAnalysisService chainAnalysis, ILogger<SignalEngine> logger)
{
    private const double RiskFreeRate = 0.065;
    private const double AssumedIv = 0.15; // sigma used for delta-based strike selection

    // Overnight gap stress test for naked (undefined-risk) structures: a GTT stop assumes a fill
    // near its trigger, which only holds for a continuous intraday move — a gap prints straight
    // through it with no chance to react (LifecycleManagerWorker doesn't even run outside market
    // hours). Naked shorts are sized so a move this size, in either direction, still stays inside
    // the per-position cap. Becomes a per-strategy config field once tunable (docs/gap-risk-management.md, item 3).
    private const double GapStressPct = 0.035;

    /// <summary>Returns the enabled strategy whose VIX band contains the live VIX, or null.</summary>
    public async Task<StrategyConfig?> ActiveStrategyAsync(Guid fundId, decimal vix, CancellationToken ct = default) =>
        await db.StrategyConfigs
            .Include(s => s.Legs)
            .Where(s => s.FundId == fundId && s.Enabled && s.VixMin <= vix && vix < s.VixMax)
            .OrderBy(s => s.VixMax - s.VixMin) // narrowest matching band wins if ranges overlap
            .FirstOrDefaultAsync(ct);

    // Width rungs offered to the operator: 1.0 = configured strikes (tightest, richest premium &
    // highest risk); lower scales push the shorts further OTM for a safer, lower-credit strangle.
    private static readonly decimal[] DeltaScales = [1.00m, 0.85m, 0.70m, 0.55m];

    /// <summary>
    /// Scans every option-chain expiry inside the active strategy's DTE window, builds a short-strangle
    /// (or configured structure) candidate at several widths per expiry, and returns the top
    /// <paramref name="maxCandidates"/> ranked by score (ATM-IV richness × return-on-risk). The operator
    /// picks one to approve. Each candidate already fits the per-position max-loss cap.
    /// </summary>
    public async Task<(List<TradeProposal> Candidates, string RejectReason)> GenerateCandidatesAsync(
        Fund fund, int maxCandidates = 5, CancellationToken ct = default)
    {
        var vix = await kite.GetIndiaVixAsync(ct);
        var config = await ActiveStrategyAsync(fund.Id, vix, ct);
        if (config == null)
        {
            var reason = $"VIX {vix:F1} — no enabled strategy covers this level. Configure a VIX band in Settings.";
            logger.LogWarning("No enabled strategy configured for VIX {Vix}", vix);
            return ([], reason);
        }
        if (config.Legs.Count == 0)
        {
            var reason = $"Strategy '{config.Name}' has no legs defined. Add legs in Settings.";
            logger.LogWarning("Strategy {Name} has no legs configured", config.Name);
            return ([], reason);
        }

        var instruments = await kite.GetNiftyInstrumentsAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var availableDtes = instruments.Select(i => i.Expiry).Distinct()
            .Select(e => (Expiry: e, Dte: e.DayNumber - today.DayNumber))
            .OrderBy(x => x.Expiry).ToList();

        var needsFar = config.Legs.Any(l => l.Expiry == ExpiryRank.Far);
        // NIFTY's monthly contract (the last expiry falling in a given calendar month) carries
        // materially deeper liquidity than the weeklies around it.
        bool IsMonthly(DateOnly expiry) => availableDtes
            .Where(x => x.Expiry.Year == expiry.Year && x.Expiry.Month == expiry.Month)
            .All(x => x.Expiry <= expiry);

        // Every expiry inside the configured DTE window is eligible — each becomes a candidate family.
        // Calendar-shaped strategies (near + far legs) are restricted to monthly expiries only: a
        // calendar's near and far legs are meant to track the same strike (see FarFor below), which
        // only holds up against a thin weekly if the market maker is actually quoting it — the
        // monthly is reliably liquid on both legs instead.
        var eligible = availableDtes
            .Where(x => x.Dte >= config.EntryDteMin && x.Dte <= config.EntryDteMax)
            .Where(x => !needsFar || IsMonthly(x.Expiry))
            .Select(x => x.Expiry).ToList();
        // Weekly compounding locks the ladder to the nearest eligible expiry so credits roll weekly.
        if (config.WeeklyCompounding && eligible.Count > 1)
            eligible = [eligible[0]];
        if (eligible.Count == 0)
        {
            var closest = availableDtes.Where(x => !needsFar || IsMonthly(x.Expiry))
                .OrderBy(x => Math.Abs(x.Dte - (config.EntryDteMin + config.EntryDteMax) / 2)).FirstOrDefault();
            var monthlyNote = needsFar ? "monthly " : "";
            var reason = $"No {monthlyNote}expiry in {config.EntryDteMin}–{config.EntryDteMax} DTE window for '{config.Name}'. "
                       + $"Nearest available: {closest.Expiry:dd-MMM-yy} ({closest.Dte}d). Widen the DTE range in Settings.";
            logger.LogWarning("No {Monthly}expiry in {Min}-{Max} DTE window for {Name}", monthlyNote, config.EntryDteMin, config.EntryDteMax, config.Name);
            return ([], reason);
        }

        // Far leg of a calendar always pairs with the *next* monthly (current-month monthly as
        // near, next-month monthly as far), never an interim weekly — see the comment above.
        DateOnly FarFor(DateOnly near) =>
            availableDtes.Where(x => x.Expiry > near && IsMonthly(x.Expiry)).Select(x => x.Expiry).FirstOrDefault();

        // Fetch the full chain for every eligible expiry (+ far counterparts) in one round-trip.
        var expiriesToQuote = new HashSet<DateOnly>(eligible);
        if (needsFar)
            foreach (var f in eligible.Select(FarFor).Where(f => f != default))
                expiriesToQuote.Add(f);
        var instByExpiry = instruments.Where(i => expiriesToQuote.Contains(i.Expiry))
            .GroupBy(i => i.Expiry).ToDictionary(g => g.Key, g => g.ToList());
        var quoteMap = (await kite.GetQuotesAsync(instByExpiry.Values.SelectMany(l => l.Select(i => i.Token)), ct))
            .ToDictionary(q => q.Token);
        double forward = await EstimateForwardAsync(instByExpiry[eligible[0]], ct);

        // Directional pre-prediction from the chain analysis (served from its short cache when
        // fresh). If the chain read fails, the scan proceeds unskewed rather than aborting.
        ChainAnalysis? chain = null;
        try { chain = await chainAnalysis.AnalyzeAsync(ct: ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Chain analysis unavailable — candidates will not be bias-skewed");
        }
        var bias = chain?.BiasScore ?? 0m;
        var biasLabel = chain?.BiasLabel;
        // Rising VIX intraday vs. the morning print — a risk-reduction brake only, never a boost.
        var vixSizeMultiplier = VixDriftSizeMultiplier(vix, chain?.MorningVix ?? 0m);

        var candidates = new List<TradeProposal>();
        var seen = new HashSet<string>(); // dedupe expiry+strike sets that collapse to the same chain
        string? lastReason = null;

        foreach (var nearExpiry in eligible)
        {
            var farExpiry = needsFar ? FarFor(nearExpiry) : default;
            if (needsFar && farExpiry == default)
            {
                lastReason = $"No far expiry available after {nearExpiry:dd-MMM-yy} for calendar '{config.Name}'.";
                continue;
            }

            foreach (var scale in DeltaScales)
            {
                var (proposal, reason) = TryBuildCandidate(fund, config, vix, nearExpiry, farExpiry,
                    instByExpiry, quoteMap, forward, today, scale, bias, biasLabel, vixSizeMultiplier);
                if (proposal == null) { lastReason = reason; continue; }

                var key = $"{proposal.ExpiryDate:yyyyMMdd}:" + string.Join(',',
                    proposal.Legs.OrderBy(l => l.Strike).Select(l => $"{l.OptionType}{l.Strike}"));
                if (seen.Add(key)) candidates.Add(proposal);
            }
        }

        if (candidates.Count == 0)
            return ([], lastReason ?? $"No '{config.Name}' candidate fits the ₹{fund.PerPositionMaxLoss:N0} per-position cap. "
                                    + "Raise the cap, lower the GTT stop %, or widen the DTE range in Settings.");

        // Rank by score (IV richness × return-on-risk), breaking ties toward the higher-IV expiry.
        var ranked = candidates
            .OrderByDescending(p => p.Score).ThenByDescending(p => p.AtmIv)
            .Take(maxCandidates).ToList();
        logger.LogInformation("VIX {Vix} → {Count} {Name} candidate(s) across {Expiries} expiries; top score {Score}",
            vix, ranked.Count, config.Name, eligible.Count, ranked[0].Score);
        return (ranked, string.Empty);
    }

    /// <summary>
    /// Builds one strangle candidate for a single expiry at a given short-leg width (deltaScale ≤ 1).
    /// Sizes lots to the per-position budget; returns null when even a single lot busts the cap so the
    /// caller can skip that width. Pure: no DB or broker round-trips (the chain is passed in).
    /// </summary>
    private (TradeProposal? Proposal, string? Reason) TryBuildCandidate(
        Fund fund, StrategyConfig config, decimal vix, DateOnly nearExpiry, DateOnly farExpiry,
        Dictionary<DateOnly, List<KiteInstrument>> instByExpiry, Dictionary<long, KiteQuote> quoteMap,
        double forward, DateOnly today, decimal deltaScale, decimal bias, string? biasLabel, decimal vixSizeMultiplier)
    {
        var nearInsts = instByExpiry[nearExpiry];
        bool needsFar = config.Legs.Any(l => l.Expiry == ExpiryRank.Far);
        var farInsts = needsFar && farExpiry != default ? instByExpiry[farExpiry] : [];
        int lotSize = fund.LotSize;
        double tNear = (nearExpiry.DayNumber - today.DayNumber) / 365.0;
        double tFar = needsFar && farExpiry != default ? (farExpiry.DayNumber - today.DayNumber) / 365.0 : tNear;

        // Effective (width-rung + chain-bias-skewed) target delta per option type's short leg,
        // computed once so a buy leg configured with the same target delta as its matching sell leg
        // — i.e. a calendar's far leg, meant to track the near leg's strike — shares exactly this
        // value instead of drifting to its own raw, unskewed strike. A buy leg with a genuinely
        // distinct target delta (e.g. an iron condor's protective wing) keeps its own, untouched.
        // Grouped (not a straight ToDictionary) because the leg editor doesn't stop an operator from
        // configuring two sell legs of the same option type — first one wins, same as the
        // FirstOrDefault used for `sellLeg` below, rather than throwing on the duplicate key.
        var sellDeltaByType = config.Legs.Where(l => l.Side == Side.Sell)
            .GroupBy(l => l.OptionType)
            .ToDictionary(g => g.Key, g => SkewedDelta(g.First().TargetDelta * deltaScale, g.Key, bias));

        var picks = new List<(StrategyLeg Cfg, KiteInstrument Inst, KiteQuote Quote)>();
        foreach (var leg in config.Legs)
        {
            var (insts, t) = leg.Expiry == ExpiryRank.Far ? (farInsts, tFar) : (nearInsts, tNear);
            decimal wantDelta;
            if (leg.Side == Side.Sell)
                wantDelta = sellDeltaByType[leg.OptionType];
            else
            {
                var sellLeg = config.Legs.FirstOrDefault(l => l.OptionType == leg.OptionType && l.Side == Side.Sell);
                wantDelta = sellLeg != null && sellLeg.TargetDelta == leg.TargetDelta
                    ? sellDeltaByType[leg.OptionType]
                    : leg.TargetDelta;
            }
            var pick = SelectByDelta(insts, quoteMap, leg.OptionType, wantDelta, forward, t);
            if (pick == null)
                return (null, $"Could not resolve {leg.Side} {leg.OptionType} ~{wantDelta:F2}Δ for '{config.Name}' "
                            + $"(expiry {nearExpiry:dd-MMM-yy}). Check that instruments have live quotes.");
            picks.Add((leg, pick.Value.Inst, pick.Value.Quote));
        }

        decimal perUnitCredit = PerUnitCredit(picks);
        var (maxLossPerUnit, _) = MaxLossPerUnit(picks, perUnitCredit, config, forward, today);
        if (maxLossPerUnit <= 0)
            return (null, $"'{config.Name}' produces zero/negative max-loss (net premium ₹{perUnitCredit:N0}/unit). "
                        + "Quotes may be stale — bid/ask are 0 outside market hours.");
        if (maxLossPerUnit * lotSize > fund.PerPositionMaxLoss)
            return (null, $"Single lot max loss ₹{maxLossPerUnit * lotSize:N0} exceeds the ₹{fund.PerPositionMaxLoss:N0} "
                        + $"cap at {deltaScale:P0} of target Δ.");

        // Size within the configured fraction of the per-position loss budget. Weekly compounding
        // scales it with fund growth so realised credits enlarge the next week's size, and a rising
        // intraday VIX shrinks it, but the hard PerPositionMaxLoss cap below always wins.
        decimal budget = fund.PerPositionMaxLoss * config.SizingPct / 100m;
        if (config.WeeklyCompounding && fund.StartingCapital > 0)
            budget *= Math.Max(1m, fund.CurrentNav / fund.StartingCapital);
        budget *= vixSizeMultiplier;
        int lots = Math.Max(1, (int)Math.Floor((double)(budget / (maxLossPerUnit * lotSize))));
        decimal maxLoss = maxLossPerUnit * lots * lotSize;
        while (lots > 1 && maxLoss > fund.PerPositionMaxLoss) { lots--; maxLoss = maxLossPerUnit * lots * lotSize; }

        int qty = lots * lotSize;
        decimal netCredit = perUnitCredit * qty;
        double atmIv = EstimateAtmIv(nearInsts, quoteMap, forward, tNear);
        int entryDte = nearExpiry.DayNumber - today.DayNumber;
        // IV-rank proxy: ATM IV relative to the VIX level (>1 ⇒ index options richer than the VIX print).
        decimal ivRank = (decimal)Math.Clamp(atmIv / Math.Max((double)vix / 100.0, 0.01), 0, 1);

        return (new TradeProposal
        {
            FundId = fund.Id,
            Strategy = config.Strategy,
            ExpiryDate = nearExpiry,
            IndiaVix = vix,
            AtmIv = (decimal)atmIv,
            IvRank = ivRank,
            Score = ScoreProposal(atmIv, perUnitCredit, maxLossPerUnit),
            ExpectedReturnPct = Math.Abs(netCredit) / fund.StartingCapital * 100,
            Rationale = $"VIX {vix:F1} in [{config.VixMin},{config.VixMax}) → {config.Name} ({config.Strategy}); "
                      + $"expiry {nearExpiry:dd-MMM-yy} ({entryDte}d), ATM IV {atmIv * 100:F1}%; "
                      + $"{(netCredit >= 0 ? "credit" : "debit")} ₹{Math.Abs(netCredit):N0}, max loss ₹{maxLoss:N0}"
                      + (config.GttEnabled ? $", GTT stop @ {config.GttPremiumPct:F0}% premium" : "")
                      + (deltaScale < 1m ? $"; shorts at {deltaScale:P0} of target Δ for a wider, lower-risk strangle" : "")
                      + (Math.Abs(bias) >= 0.15m && biasLabel != null ? $"; chain bias {biasLabel} ({bias:+0.00;-0.00}) — short strikes skewed to the safer side" : "")
                      + (config.WeeklyCompounding ? "; weekly compounding — nearest expiry, size scaled with NAV" : "")
                      + (vixSizeMultiplier < 1m ? $"; VIX rising intraday vs. the morning open — size cut to {vixSizeMultiplier:P0}" : ""),
            EntryDte = entryDte,
            TargetExitDte = config.TargetExitDte,
            Lots = lots,
            Qty = qty,
            NetCredit = netCredit,
            MaxLoss = maxLoss,
            GttEnabled = config.GttEnabled,
            GttPremiumPct = config.GttPremiumPct,
            ProfitTargetPct = config.ProfitTargetPct,
            AdjustTriggerDelta = config.AdjustTriggerDelta,
            Legs = picks.Select(r => ToProposalLeg(r.Inst, r.Quote, r.Cfg.Side)).ToList()
        }, null);
    }

    // Per-unit net premium: use last traded price when bid/ask are stale (e.g. pre-market).
    private static decimal PerUnitCredit(List<(StrategyLeg Cfg, KiteInstrument Inst, KiteQuote Quote)> legs) =>
        legs.Sum(r =>
        {
            var mid = r.Quote.Bid > 0 && r.Quote.Ask > 0 ? (r.Quote.Bid + r.Quote.Ask) / 2 : r.Quote.Ltp;
            return (r.Cfg.Side == Side.Sell ? 1 : -1) * mid;
        });

    /// <summary>
    /// Computes the per-unit max loss and whether the structure is defined-risk.
    /// Winged structures (iron condor / vertical — including a "double calendar" that is really a
    /// single-expiry debit condor) risk the wider wing less net premium; for net-debit structures a
    /// GTT stop at <see cref="StrategyConfig.GttPremiumPct"/>% of the debit (2× by default) caps that.
    /// A same-strike calendar risks the debit paid; a naked credit is sized to the worse of the GTT
    /// premium multiple and a gap-stress re-pricing (see <see cref="GapStressLoss"/>).
    /// </summary>
    private static (decimal MaxLossPerUnit, bool IsDefinedRisk) MaxLossPerUnit(
        List<(StrategyLeg Cfg, KiteInstrument Inst, KiteQuote Quote)> legs, decimal perUnitCredit, StrategyConfig config,
        double forward, DateOnly today)
    {
        decimal Width(OptionType type, bool callSide)
        {
            var shorts = legs.Where(l => l.Cfg.OptionType == type && l.Cfg.Side == Side.Sell).ToList();
            var longs = legs.Where(l => l.Cfg.OptionType == type && l.Cfg.Side == Side.Buy).ToList();
            if (shorts.Count == 0 || longs.Count == 0) return 0;
            // CE wing is above the short; PE wing is below.
            return callSide
                ? longs.Min(l => l.Inst.Strike) - shorts.Max(l => l.Inst.Strike)
                : shorts.Min(l => l.Inst.Strike) - longs.Max(l => l.Inst.Strike);
        }

        decimal maxWidth = Math.Max(Width(OptionType.CE, true), Width(OptionType.PE, false));
        if (maxWidth > 0)
        {
            // Defined-risk by wings: worst case is the wider wing less net premium (debit adds, credit subtracts).
            decimal structural = Math.Max(maxWidth - perUnitCredit, 0.01m);

            // For a net-debit condor, a real GTT stop at GttPremiumPct% of the debit (2× by default)
            // caps realised loss below the wing-width worst case. Only when an actual stop is placed.
            if (perUnitCredit < 0 && config.GttEnabled)
            {
                decimal stopCap = -perUnitCredit * config.GttPremiumPct / 100m;
                structural = Math.Max(Math.Min(structural, stopCap), 0.01m);
            }
            return (structural, true);
        }

        if (perUnitCredit <= 0)
            return (-perUnitCredit, true); // same-strike calendar: debit paid is the max loss

        // Naked short credit — sized to the worse of two estimates: the GTT stop multiple of
        // premium (holds only if the stop can actually fill near its trigger) and a gap-stress
        // re-pricing of the short legs at a large adverse overnight move (the case a GTT cannot
        // protect against at all, since there is no continuous price for it to trigger on).
        decimal stopMult = config.GttEnabled ? config.GttPremiumPct / 100m : 2m;
        decimal stopCapLoss = perUnitCredit * stopMult;
        decimal gapStressLoss = GapStressLoss(legs, forward, today);
        return (Math.Max(stopCapLoss, gapStressLoss), false);
    }

    /// <summary>
    /// Re-prices every short leg of a naked structure at spot stressed by ±<see cref="GapStressPct"/>
    /// (each leg's own market-implied vol, held constant — a real gap likely also expands vol, so
    /// this is a floor, not a ceiling) and returns the worse-direction buy-back cost less the credit
    /// received. Only called for structures with no offsetting long leg (see caller).
    /// </summary>
    private static decimal GapStressLoss(
        List<(StrategyLeg Cfg, KiteInstrument Inst, KiteQuote Quote)> legs, double forward, DateOnly today)
    {
        var shorts = legs.Where(l => l.Cfg.Side == Side.Sell).ToList();
        if (shorts.Count == 0) return 0m;

        decimal CostAtStress(double stressedForward)
        {
            decimal cost = 0m;
            foreach (var (cfg, inst, quote) in shorts)
            {
                double t = Math.Max((inst.Expiry.DayNumber - today.DayNumber) / 365.0, 1.0 / 365.0);
                bool isCall = cfg.OptionType == OptionType.CE;
                double mid = quote.Bid > 0 && quote.Ask > 0 ? (double)(quote.Bid + quote.Ask) / 2 : (double)quote.Ltp;
                double legIv = Black76.SolveIv(mid, forward, (double)inst.Strike, t, RiskFreeRate, isCall);
                if (legIv <= 0) legIv = AssumedIv; // illiquid/stale quote — fall back to the flat assumption
                var g = isCall
                    ? Black76.ComputeCall(stressedForward, (double)inst.Strike, t, RiskFreeRate, legIv)
                    : Black76.ComputePut(stressedForward, (double)inst.Strike, t, RiskFreeRate, legIv);
                cost += (decimal)g.Price;
            }
            return cost;
        }

        decimal entryCredit = shorts.Sum(l => l.Quote.Bid > 0 && l.Quote.Ask > 0 ? (l.Quote.Bid + l.Quote.Ask) / 2 : l.Quote.Ltp);
        decimal worstCost = Math.Max(CostAtStress(forward * (1 + GapStressPct)), CostAtStress(forward * (1 - GapStressPct)));
        return Math.Max(worstCost - entryCredit, 0m);
    }

    // Chain-bias strike skew for short legs: a bullish bias (bias > 0) keeps short puts nearer
    // (higher Δ, richer premium on the defended side) and pushes short calls wider; bearish does
    // the reverse. Shift is capped at ±30% of the target delta and clamped to a tradable range.
    private static decimal SkewedDelta(decimal target, OptionType type, decimal bias)
    {
        if (bias == 0) return target;
        decimal shift = 0.30m * bias * (type == OptionType.PE ? 1 : -1);
        return Math.Clamp(target * (1 + shift), 0.03m, 0.60m);
    }

    // VIX rising intraday vs. the morning print is the same "stay conservative, size writing down"
    // signal ChainAnalysisService already renders as advisory text (its vixTrend string) — this
    // turns it into an actual size cut. Mirrors that method's 1% dead-zone so noise doesn't trigger
    // it, and only ever brakes: a falling/flat VIX applies no size boost, since sizing up on a
    // "green light" read is a separate decision left to the operator, not something to automate.
    private static decimal VixDriftSizeMultiplier(decimal vix, decimal morningVix)
    {
        if (morningVix <= 0) return 1m;
        decimal drift = (vix - morningVix) / morningVix;
        if (drift <= 0.01m) return 1m;
        return Math.Clamp(1m - drift * 2m, 0.5m, 1m);
    }

    private static (KiteInstrument Inst, KiteQuote Quote)? SelectByDelta(
        List<KiteInstrument> instruments, Dictionary<long, KiteQuote> quotes, OptionType type, decimal targetDelta, double forward, double t)
    {
        var optType = type == OptionType.CE ? "CE" : "PE";
        var best = instruments
            .Where(i => i.OptionType == optType && quotes.TryGetValue(i.Token, out var q) && q.Ltp > 0)
            .Select(i =>
            {
                var g = optType == "CE"
                    ? Black76.ComputeCall(forward, (double)i.Strike, t, RiskFreeRate, AssumedIv)
                    : Black76.ComputePut(forward, (double)i.Strike, t, RiskFreeRate, AssumedIv);
                return (Inst: i, Quote: quotes[i.Token], AbsDelta: Math.Abs(g.Delta));
            })
            .OrderBy(x => Math.Abs(x.AbsDelta - (double)targetDelta))
            .FirstOrDefault();

        return best.Inst == null ? null : (best.Inst, best.Quote);
    }

    private async Task<double> EstimateForwardAsync(List<KiteInstrument> nearInsts, CancellationToken ct)
    {
        // For index options the forward ≈ spot; fall back to the median strike if the spot call fails.
        try { return (double)await kite.GetNiftySpotAsync(ct); }
        catch
        {
            var strikes = nearInsts.Select(i => (double)i.Strike).OrderBy(s => s).ToList();
            return strikes.Count > 0 ? strikes[strikes.Count / 2] : 24000;
        }
    }

    private static double EstimateAtmIv(List<KiteInstrument> nearInsts, Dictionary<long, KiteQuote> quotes, double forward, double t)
    {
        var atm = nearInsts
            .Where(i => i.OptionType == "CE")
            .OrderBy(i => Math.Abs((double)i.Strike - forward))
            .FirstOrDefault();
        if (atm == null || !quotes.TryGetValue(atm.Token, out var q) || q.Ltp <= 0) return 0.15;
        return Black76.SolveIv((double)q.Ltp, forward, (double)atm.Strike, t, RiskFreeRate, isCall: true);
    }

    private static ProposalLeg ToProposalLeg(KiteInstrument inst, KiteQuote quote, Side side) =>
        new()
        {
            OptionType = inst.OptionType == "CE" ? OptionType.CE : OptionType.PE,
            Side = side,
            Strike = inst.Strike,
            ExpiryDate = inst.Expiry,
            TradingSymbol = inst.Symbol,
            InstrumentToken = inst.Token,
            MidPrice = (quote.Bid + quote.Ask) / 2,
        };

    // Ranks candidates so the operator's "max IV, sensible risk" preference surfaces first:
    // ATM-IV richness (a seller wants high IV) blended with return-on-risk (credit per ₹ of max loss).
    private static decimal ScoreProposal(double atmIv, decimal perUnitCredit, decimal maxLossPerUnit)
    {
        decimal ivScore = (decimal)Math.Clamp(atmIv / 0.30, 0, 1);                            // 30% ATM IV ⇒ full marks
        decimal rorScore = Math.Clamp(Math.Abs(perUnitCredit) / maxLossPerUnit, 0, 1);        // return on risk
        return Math.Round(ivScore * 0.5m + rorScore * 0.5m, 2);
    }
}
