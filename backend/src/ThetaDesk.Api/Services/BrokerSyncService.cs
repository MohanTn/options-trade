using Microsoft.EntityFrameworkCore;
using ThetaDesk.Data;
using ThetaDesk.Api.Kite;
using ThetaDesk.Domain.Entities;
using ThetaDesk.Domain.Enums;

namespace ThetaDesk.Api.Services;

/// <summary>
/// Reconciles the local DB against the broker's live positions: updates unrealised P&L on
/// already-tracked positions and ingests any untracked broker positions (e.g. trades placed
/// directly in Kite, or pre-existing structures discovered on session start), classifying the
/// strategy and filling all leg data.
/// </summary>
public class BrokerSyncService(ThetaDeskDbContext db, IKiteClient kite, ILogger<BrokerSyncService> logger)
{
    // Statuses whose legs are considered "already tracked": their broker P&L is synced and they are
    // excluded from untracked-ingestion. PendingFill is included so that once the operator places an
    // approved-but-unfilled basket at the broker, it reconciles onto the existing position instead of
    // being ingested as a duplicate.
    private static readonly PositionStatus[] TrackedStatuses =
        [PositionStatus.PendingFill, PositionStatus.Open, PositionStatus.AutoAdjusting, PositionStatus.ProfitTaking, PositionStatus.RiskStopping];

    /// <summary>Returns the number of new positions ingested from the broker.</summary>
    public async Task<int> SyncAsync(Fund fund, CancellationToken ct)
    {
        var brokerPositions = await kite.GetPositionsAsync(ct);
        var nfoOptions = brokerPositions.Where(p => p.Exchange == "NFO" && p.Quantity != 0).ToList();
        if (nfoOptions.Count == 0) return 0;

        var dbPositions = await db.Positions.Include(p => p.Legs)
            .Where(p => TrackedStatuses.Contains(p.Status))
            .ToListAsync(ct);

        // TryAdd: if the same token appears in multiple open positions, first one wins — no crash
        var tokenToPosition = new Dictionary<long, Position>();
        foreach (var pos in dbPositions)
            foreach (var leg in pos.Legs)
                tokenToPosition.TryAdd(leg.InstrumentToken, pos);

        // Sum P&L across all broker legs per DB position, then apply
        var pnlByPosition = new Dictionary<Guid, decimal>();
        foreach (var bp in nfoOptions.Where(bp => tokenToPosition.ContainsKey(bp.InstrumentToken)))
        {
            var pos = tokenToPosition[bp.InstrumentToken];
            pnlByPosition.TryGetValue(pos.Id, out var current);
            pnlByPosition[pos.Id] = current + bp.UnrealisedPnl;
        }
        foreach (var (posId, pnl) in pnlByPosition)
            dbPositions.First(p => p.Id == posId).UnrealisedPnl = pnl;

        var ingested = await IngestUntrackedAsync(fund, nfoOptions, tokenToPosition, ct);

        await db.SaveChangesAsync(ct);
        return ingested;
    }

    private async Task<int> IngestUntrackedAsync(
        Fund fund, List<BrokerPosition> nfoOptions, Dictionary<long, Position> tokenToPosition, CancellationToken ct)
    {
        var instruments = await kite.GetNiftyInstrumentsAsync(ct); // cached after first call
        var instrumentMap = instruments.ToDictionary(i => i.Token);

        var untracked = nfoOptions
            .Where(bp => !tokenToPosition.ContainsKey(bp.InstrumentToken) && instrumentMap.ContainsKey(bp.InstrumentToken))
            .ToList();
        if (untracked.Count == 0) return 0;

        var untrackedTokens = untracked.Select(bp => bp.InstrumentToken).ToList();
        var existingInstrTokens = (await db.Instruments
            .Where(i => untrackedTokens.Contains(i.InstrumentToken))
            .Select(i => i.InstrumentToken)
            .ToListAsync(ct)).ToHashSet();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // A position spanning more than one expiry is a calendar structure and must stay one
        // position; single-expiry legs are grouped per expiry so independent trades stay separate.
        var spansMultipleExpiries = untracked.Select(bp => instrumentMap[bp.InstrumentToken].Expiry).Distinct().Count() > 1;
        var groups = spansMultipleExpiries
            ? [untracked]
            : untracked.GroupBy(bp => instrumentMap[bp.InstrumentToken].Expiry).Select(g => g.ToList());

        var ingested = 0;
        foreach (var legs in groups)
        {
            var (strategy, isDefinedRisk) = Classify(legs, instrumentMap);
            var expiryDate = legs.Min(bp => instrumentMap[bp.InstrumentToken].Expiry); // nearest expiry drives DTE/exit
            var dte = Math.Max(expiryDate.DayNumber - today.DayNumber, 0);

            // Estimate margin via the basket API so PaperKiteClient.GetMarginSummaryAsync
            // can sum DB MarginBlocked accurately (live mode reads real margin from Zerodha directly).
            decimal marginBlocked = 0;
            try
            {
                var basketLegs = legs.Select(bp => new BasketLeg(
                    Symbol: bp.TradingSymbol, Exchange: "NFO",
                    TransactionType: bp.Quantity < 0 ? "SELL" : "BUY",
                    Qty: Math.Abs(bp.Quantity), Product: "NRML", OrderType: "MARKET"));
                var km = await kite.GetBasketMarginAsync(basketLegs, ct);
                marginBlocked = km.Total;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not estimate basket margin for ingested {Strategy} position — MarginBlocked will be 0", strategy);
            }

            var position = new Position
            {
                FundId = fund.Id,
                Strategy = strategy,
                IsDefinedRisk = isDefinedRisk,
                Status = PositionStatus.Open,
                EntryDate = today,
                ExpiryDate = expiryDate,
                EntryDte = dte,
                NetCredit = legs.Sum(bp => bp.Quantity < 0 ? bp.AveragePrice * Math.Abs(bp.Quantity) : -bp.AveragePrice * bp.Quantity),
                MaxLoss = fund.PerPositionMaxLoss, // fall back to fund cap so an ingested position isn't risk-stopped at zero loss
                MarginBlocked = marginBlocked,
                UnrealisedPnl = legs.Sum(bp => bp.UnrealisedPnl),
            };
            db.Positions.Add(position);

            foreach (var bp in legs)
            {
                var inst = instrumentMap[bp.InstrumentToken];
                var optionType = inst.OptionType == "CE" ? OptionType.CE : OptionType.PE;
                if (!existingInstrTokens.Contains(bp.InstrumentToken))
                {
                    db.Instruments.Add(new Instrument
                    {
                        InstrumentToken = bp.InstrumentToken,
                        TradingSymbol = bp.TradingSymbol,
                        Strike = inst.Strike,
                        ExpiryDate = inst.Expiry,
                        OptionType = optionType,
                        LotSize = fund.LotSize,
                    });
                    existingInstrTokens.Add(bp.InstrumentToken);
                }
                db.OptionLegs.Add(new OptionLeg
                {
                    PositionId = position.Id,
                    InstrumentToken = bp.InstrumentToken,
                    OptionType = optionType,
                    Side = bp.Quantity < 0 ? Side.Sell : Side.Buy,
                    Strike = inst.Strike,
                    // Derive lots from the broker quantity using the fund's configured lot size (single source of truth).
                    Lots = Math.Abs(bp.Quantity) / fund.LotSize,
                    Qty = Math.Abs(bp.Quantity),
                    EntryPrice = bp.AveragePrice,
                    CurrentPrice = bp.LastPrice,
                });
            }

            ingested++;
            logger.LogInformation("Ingested {Strategy} position from broker: {LegCount} legs, expiry {Expiry}, DTE {Dte}",
                strategy, legs.Count, expiryDate, dte);
        }

        return ingested;
    }

    /// <summary>
    /// Classifies a set of broker legs by structure: multiple expiries ⇒ calendar (double calendar);
    /// both option types within one expiry ⇒ iron condor if it has long wings, else short strangle;
    /// a single option type ⇒ vertical credit spread.
    /// </summary>
    private static (StrategyType Strategy, bool IsDefinedRisk) Classify(
        List<BrokerPosition> legs, Dictionary<long, KiteInstrument> instrumentMap)
    {
        var multiExpiry = legs.Select(bp => instrumentMap[bp.InstrumentToken].Expiry).Distinct().Count() > 1;
        if (multiExpiry)
            return (StrategyType.DoubleCalendar, true);

        var hasCe = legs.Any(bp => instrumentMap[bp.InstrumentToken].OptionType == "CE");
        var hasPe = legs.Any(bp => instrumentMap[bp.InstrumentToken].OptionType == "PE");
        if (hasCe && hasPe)
        {
            var hasLongWing = legs.Any(bp => bp.Quantity > 0);
            return hasLongWing ? (StrategyType.IronCondor, true) : (StrategyType.ShortStrangle, false);
        }

        return (StrategyType.CreditSpread, legs.Any(bp => bp.Quantity > 0));
    }
}
