namespace ThetaDesk.Api.Kite;

public record KiteSessionStatus(bool Valid, string? LoginUrl, DateTime? ExpiresAt);
public record KiteInstrument(long Token, string Symbol, decimal Strike, DateOnly Expiry, string OptionType, int LotSize);
public record KiteQuote(long Token, decimal Ltp, decimal Bid, decimal Ask, long Oi, decimal Iv, long Volume = 0);
public record KiteMargin(decimal Span, decimal Exposure, decimal Total);
// AvgFillPrice is set only when the order is known to be filled at placement time
// (e.g. simulated MARKET fills in paper mode). Live orders fill asynchronously, so it stays null.
public record KiteOrderResult(string OrderId, bool Success, string? Error, decimal? AvgFillPrice = null);
public record KiteGttResult(string GttId, bool Success, string? Error);

public interface IKiteClient
{
    Task<KiteSessionStatus> GetSessionStatusAsync();
    Task<string> ExchangeTokenAsync(string requestToken, CancellationToken ct = default);
    Task ClearSessionAsync(CancellationToken ct = default);
    Task<decimal> GetIndiaVixAsync(CancellationToken ct = default);
    Task<IReadOnlyList<KiteInstrument>> GetNiftyInstrumentsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<KiteQuote>> GetQuotesAsync(IEnumerable<long> tokens, CancellationToken ct = default);
    Task<KiteMargin> GetBasketMarginAsync(IEnumerable<BasketLeg> legs, CancellationToken ct = default);
    Task<KiteOrderResult> PlaceOrderAsync(OrderRequest req, CancellationToken ct = default);
    Task<KiteGttResult> PlaceGttStopAsync(GttRequest req, CancellationToken ct = default);
    Task<bool> CancelGttAsync(string gttId, CancellationToken ct = default);
    Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default);
    Task<decimal> GetFundsAsync(CancellationToken ct = default);
    Task<MarginSummary> GetMarginSummaryAsync(CancellationToken ct = default);
    Task<decimal> GetNiftySpotAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BrokerPosition>> GetPositionsAsync(CancellationToken ct = default);
}

public record BasketLeg(string Symbol, string Exchange, string TransactionType, int Qty, string Product, string OrderType);
public record OrderRequest(string Symbol, string Exchange, string TransactionType, int Qty, string Product, string OrderType, decimal? Price = null, string? Tag = null);
public record GttRequest(string Symbol, string Exchange, decimal TriggerPrice, int Qty, string TransactionType);

public record BrokerPosition(
    long InstrumentToken,
    string TradingSymbol,
    string Exchange,
    string Product,
    int Quantity,
    decimal AveragePrice,
    decimal LastPrice,
    decimal UnrealisedPnl,
    decimal RealisedPnl);

public record MarginSummary(
    decimal TotalCapital,      // Cash + collateral (total deployable capital)
    decimal AvailableBalance,  // live_balance — what can actually be used for new trades right now
    decimal Span,              // SPAN margin blocked by the exchange
    decimal Exposure,          // Exposure margin (Zerodha additional buffer)
    decimal OptionPremium,     // Premium blocked for long legs
    decimal Collateral         // Collateral from pledged securities
)
{
    public decimal UsedMargin => Span + Exposure;
};
