using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using ThetaDesk.Data;
using ThetaDesk.Domain.Enums;

namespace ThetaDesk.Api.Kite;

/// <summary>
/// Paper-trading broker adapter. All <b>market-data</b> calls are forwarded to the real
/// <see cref="KiteClient"/> (live quotes/VIX/instruments/margins from Zerodha), while all
/// <b>execution</b> calls are simulated: orders fill instantly against live bid/ask into a
/// Redis-backed fill ledger, GTT/cancels are no-ops returning synthetic ids, and
/// <see cref="GetPositionsAsync"/> reprices that ledger off live quotes.
/// Active only when <c>Kite:PaperTrading=true</c>; a real Zerodha session is still required for data.
/// </summary>
public class PaperKiteClient(KiteClient inner, IConnectionMultiplexer redis, IServiceScopeFactory scopeFactory, ILogger<PaperKiteClient> logger) : IKiteClient
{
    private const string LedgerKey = "paper:fills";

    // One leg of the simulated portfolio. NetQty is signed (long > 0, short < 0); AvgPrice is the VWAP.
    private record PaperFill(string Symbol, int NetQty, decimal AvgPrice);

    // ── Market data — forwarded live to the real broker ──────────────────────────────────
    public Task<KiteSessionStatus> GetSessionStatusAsync() => inner.GetSessionStatusAsync();
    public Task<string> ExchangeTokenAsync(string requestToken, CancellationToken ct = default) => inner.ExchangeTokenAsync(requestToken, ct);
    public Task ClearSessionAsync(CancellationToken ct = default) => inner.ClearSessionAsync(ct);
    public Task<decimal> GetIndiaVixAsync(CancellationToken ct = default) => inner.GetIndiaVixAsync(ct);
    public Task<IReadOnlyList<KiteInstrument>> GetNiftyInstrumentsAsync(CancellationToken ct = default) => inner.GetNiftyInstrumentsAsync(ct);
    public Task<IReadOnlyList<KiteQuote>> GetQuotesAsync(IEnumerable<long> tokens, CancellationToken ct = default) => inner.GetQuotesAsync(tokens, ct);
    public Task<KiteMargin> GetBasketMarginAsync(IEnumerable<BasketLeg> legs, CancellationToken ct = default) => inner.GetBasketMarginAsync(legs, ct);
    public async Task<decimal> GetFundsAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ThetaDeskDbContext>();
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        return fund?.CashBalance ?? 0;
    }

    public async Task<MarginSummary> GetMarginSummaryAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ThetaDeskDbContext>();
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        var usedMargin = await db.Positions
            .Where(p => p.Status == PositionStatus.Open || p.Status == PositionStatus.AutoAdjusting)
            .SumAsync(p => p.MarginBlocked, ct);
        var totalCapital = fund?.CashBalance ?? 0;
        return new MarginSummary(
            TotalCapital: totalCapital,
            AvailableBalance: totalCapital - usedMargin,
            Span: usedMargin,   // paper mode: all blocked margin attributed to SPAN
            Exposure: 0,
            OptionPremium: 0,
            Collateral: 0
        );
    }
    public Task<decimal> GetNiftySpotAsync(CancellationToken ct = default) => inner.GetNiftySpotAsync(ct);

    // ── Execution — simulated ────────────────────────────────────────────────────────────

    public async Task<KiteOrderResult> PlaceOrderAsync(OrderRequest req, CancellationToken ct = default)
    {
        var instrument = await ResolveInstrumentAsync(req.Symbol, ct);
        if (instrument == null)
            return new KiteOrderResult("", false, $"Paper: unknown symbol {req.Symbol}");

        var quote = (await inner.GetQuotesAsync([instrument.Token], ct)).FirstOrDefault();
        if (quote == null)
            return new KiteOrderResult("", false, $"Paper: no quote for {req.Symbol}");

        var isBuy = req.TransactionType.Equals("BUY", StringComparison.OrdinalIgnoreCase);
        // Cross the spread: pay the ask to buy, hit the bid to sell — models real entry slippage.
        var fill = isBuy ? (quote.Ask > 0 ? quote.Ask : quote.Ltp) : (quote.Bid > 0 ? quote.Bid : quote.Ltp);
        if (fill <= 0)
            return new KiteOrderResult("", false, $"Paper: no tradable price for {req.Symbol}");

        var signedQty = isBuy ? req.Qty : -req.Qty;
        ApplyFill(instrument.Token, instrument.Symbol, signedQty, fill);

        var orderId = $"PAPER-{Guid.NewGuid():N}";
        logger.LogInformation("Paper fill {Side} {Qty} {Symbol} @ {Fill} (order {OrderId})",
            req.TransactionType, req.Qty, instrument.Symbol, fill, orderId);
        return new KiteOrderResult(orderId, true, null, fill);
    }

    /// <summary>
    /// Seeds the simulated ledger from the operator's real broker holdings so a paper session can
    /// start from the actual book (e.g. a double calendar placed directly in Kite). Idempotent:
    /// tokens already in the ledger are left untouched, so simulated trades are never clobbered.
    /// </summary>
    public async Task SeedFromBrokerAsync(CancellationToken ct = default)
    {
        var real = await inner.GetPositionsAsync(ct);
        var db = redis.GetDatabase();
        foreach (var p in real.Where(p => p.Exchange == "NFO" && p.Quantity != 0))
        {
            var field = p.InstrumentToken.ToString();
            if (await db.HashExistsAsync(LedgerKey, field)) continue;
            await db.HashSetAsync(LedgerKey, field,
                JsonSerializer.Serialize(new PaperFill(p.TradingSymbol, p.Quantity, p.AveragePrice)));
            logger.LogInformation("Seeded paper book from broker: {Symbol} qty {Qty} @ {Avg}",
                p.TradingSymbol, p.Quantity, p.AveragePrice);
        }
    }

    public Task<KiteGttResult> PlaceGttStopAsync(GttRequest req, CancellationToken ct = default) =>
        Task.FromResult(new KiteGttResult($"PAPER-GTT-{Guid.NewGuid():N}", true, null));

    public Task<bool> CancelGttAsync(string gttId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.FromResult(true);

    public async Task<IReadOnlyList<BrokerPosition>> GetPositionsAsync(CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var entries = await db.HashGetAllAsync(LedgerKey);
        var fills = entries
            .Select(e => (Token: long.Parse(e.Name!), Fill: JsonSerializer.Deserialize<PaperFill>(e.Value!)!))
            .Where(x => x.Fill.NetQty != 0)
            .ToList();
        if (fills.Count == 0) return [];

        var quotes = (await inner.GetQuotesAsync(fills.Select(f => f.Token), ct)).ToDictionary(q => q.Token);

        return fills.Select(x =>
        {
            var ltp = quotes.TryGetValue(x.Token, out var q) ? q.Ltp : x.Fill.AvgPrice;
            // Signed NetQty makes this correct for both sides: a short (NetQty<0) loses as price rises.
            var unrealised = (ltp - x.Fill.AvgPrice) * x.Fill.NetQty;
            return new BrokerPosition(
                x.Token, x.Fill.Symbol, "NFO", "NRML",
                x.Fill.NetQty, x.Fill.AvgPrice, ltp, unrealised, 0);
        }).ToList();
    }

    // ── Ledger helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a signed fill to the ledger using standard average-cost accounting:
    /// adding to a side updates the VWAP, reducing keeps the VWAP, flipping resets it to the new fill.
    /// Single-operator system, so the read-modify-write is not guarded against concurrent writers.
    /// </summary>
    private void ApplyFill(long token, string symbol, int signedQty, decimal fill)
    {
        var db = redis.GetDatabase();
        var field = token.ToString();
        var existing = db.HashGet(LedgerKey, field);

        PaperFill updated;
        if (existing.IsNullOrEmpty)
        {
            updated = new PaperFill(symbol, signedQty, fill);
        }
        else
        {
            var prev = JsonSerializer.Deserialize<PaperFill>(existing!)!;
            var newNet = prev.NetQty + signedQty;
            decimal avg;
            if (prev.NetQty == 0 || Math.Sign(prev.NetQty) == Math.Sign(signedQty))
                avg = (prev.AvgPrice * Math.Abs(prev.NetQty) + fill * Math.Abs(signedQty)) / Math.Abs(newNet); // adding
            else if (Math.Abs(signedQty) <= Math.Abs(prev.NetQty))
                avg = prev.AvgPrice;                                                                            // reducing
            else
                avg = fill;                                                                                     // flipped
            updated = new PaperFill(symbol, newNet, avg);
        }

        if (updated.NetQty == 0)
            db.HashDelete(LedgerKey, field);
        else
            db.HashSet(LedgerKey, field, JsonSerializer.Serialize(updated));
    }

    /// <summary>Resolve an order's symbol — either a tradingsymbol (entry) or a numeric token (close) — to an instrument.</summary>
    private async Task<KiteInstrument?> ResolveInstrumentAsync(string symbol, CancellationToken ct)
    {
        var instruments = await inner.GetNiftyInstrumentsAsync(ct); // cached after first call
        if (long.TryParse(symbol, out var token))
        {
            var byToken = instruments.FirstOrDefault(i => i.Token == token);
            if (byToken != null) return byToken;
        }
        return instruments.FirstOrDefault(i => i.Symbol == symbol);
    }
}
