using ThetaDesk.Domain.Enums;

namespace ThetaDesk.Domain.Entities;

public class Fund
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public decimal StartingCapital { get; set; }
    public decimal CashBalance { get; set; }
    public decimal CurrentNav { get; set; }
    public decimal MonthlyTargetPct { get; set; } = 3.5m;
    public decimal MaxMarginUtilPct { get; set; } = 70m;
    public decimal PerPositionMaxLoss { get; set; } = 17000m;
    public decimal DrawdownStopPct { get; set; } = 6m;
    public decimal ProfitTakePct { get; set; } = 50m;
    public int LotSize { get; set; } = 65; // NIFTY F&O contract lot size; operator-configurable.
    public ICollection<Position> Positions { get; set; } = [];
    public ICollection<TradeProposal> Proposals { get; set; } = [];
    public ICollection<NavSnapshot> NavSnapshots { get; set; } = [];
    public ICollection<RiskLimit> RiskLimits { get; set; } = [];
    public ICollection<VolatilitySnapshot> VolatilitySnapshots { get; set; } = [];
    public ICollection<Alert> Alerts { get; set; } = [];
    public ICollection<AuditEntry> AuditLog { get; set; } = [];
    public ICollection<StrategyConfig> Strategies { get; set; } = [];
}

/// <summary>
/// Operator-configurable strategy bound to a VIX regime. The signal engine activates the enabled
/// config whose [VixMin, VixMax) range contains the live VIX and builds proposals from its legs.
/// </summary>
public class StrategyConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public StrategyType Strategy { get; set; }

    // VIX regime this strategy trades in — matched as VixMin ≤ vix < VixMax.
    public decimal VixMin { get; set; }
    public decimal VixMax { get; set; }

    // Entry window and sizing.
    public int EntryDteMin { get; set; } = 42;
    public int EntryDteMax { get; set; } = 50;
    public decimal SizingPct { get; set; } = 100m; // % of the per-position max-loss budget to deploy

    // GTT protective stop.
    public bool GttEnabled { get; set; }
    public decimal GttPremiumPct { get; set; } = 200m; // trigger at this % of entry premium

    // Management tactic — copied onto each Position at entry so the lifecycle worker can act on it.
    public decimal ProfitTargetPct { get; set; } = 50m;
    public int TargetExitDte { get; set; } = 21;
    public decimal AdjustTriggerDelta { get; set; } = 0.30m;

    public ICollection<StrategyLeg> Legs { get; set; } = [];
}

/// <summary>One leg template of a <see cref="StrategyConfig"/>: which option to trade and how.</summary>
public class StrategyLeg
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StrategyConfigId { get; set; }
    public StrategyConfig StrategyConfig { get; set; } = null!;
    public OptionType OptionType { get; set; }     // CE / PE
    public Side Side { get; set; }                 // Buy / Sell
    public decimal TargetDelta { get; set; }       // strike selected nearest this |delta| (0.50 ≈ ATM)
    public ExpiryRank Expiry { get; set; } = ExpiryRank.Near; // Near or Far expiry (Far drives calendars)
}

public class Position
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public Guid? ProposalId { get; set; }
    public TradeProposal? Proposal { get; set; }
    public StrategyType Strategy { get; set; }
    public bool IsDefinedRisk { get; set; }
    public PositionStatus Status { get; set; } = PositionStatus.PendingFill;
    public DateOnly EntryDate { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public int EntryDte { get; set; }
    public int TargetExitDte { get; set; } = 21;
    public decimal NetCredit { get; set; }
    public decimal MaxLoss { get; set; }
    public decimal MarginBlocked { get; set; }
    public decimal RealisedPnl { get; set; }
    public decimal UnrealisedPnl { get; set; }
    public string? GttStopOrderId { get; set; }
    public decimal StopLossPremiumMult { get; set; } = 2m;
    // Management tactic copied from the StrategyConfig at entry (drives the lifecycle worker).
    public decimal ProfitTakePct { get; set; } = 50m;
    public decimal AdjustTriggerDelta { get; set; } = 0.30m;
    public ICollection<OptionLeg> Legs { get; set; } = [];
    public ICollection<GreeksSnapshot> GreeksSnapshots { get; set; } = [];
    public ICollection<Adjustment> Adjustments { get; set; } = [];
    public MarginSnapshot? MarginSnapshot { get; set; }
}

public class OptionLeg
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PositionId { get; set; }
    public Position Position { get; set; } = null!;
    public long InstrumentToken { get; set; }
    public Instrument Instrument { get; set; } = null!;
    public OptionType OptionType { get; set; }
    public Side Side { get; set; }
    public decimal Strike { get; set; }
    public int Lots { get; set; }
    public int Qty { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public ICollection<Order> Orders { get; set; } = [];
}

public class Instrument
{
    public long InstrumentToken { get; set; }
    public string TradingSymbol { get; set; } = string.Empty;
    public decimal Strike { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public OptionType OptionType { get; set; }
    public int LotSize { get; set; }
    public ICollection<OptionLeg> Legs { get; set; } = [];
}

public class GreeksSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PositionId { get; set; }
    public Position Position { get; set; } = null!;
    public DateTime TakenAtUtc { get; set; } = DateTime.UtcNow;
    public decimal Delta { get; set; }
    public decimal Gamma { get; set; }
    public decimal Theta { get; set; }
    public decimal Vega { get; set; }
    public decimal Rho { get; set; }
    public decimal Iv { get; set; }
    public decimal UnderlyingSpot { get; set; }
}

public class MarginSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PositionId { get; set; }
    public Position Position { get; set; } = null!;
    public decimal Span { get; set; }
    public decimal Exposure { get; set; }
    public decimal Total { get; set; }
    public decimal UtilisationPct { get; set; }
}

public class RiskLimit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public LimitScope Scope { get; set; }
    public string Metric { get; set; } = string.Empty;
    public decimal? LowerBound { get; set; }
    public decimal? UpperBound { get; set; }
    public bool Hard { get; set; }
}

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OptionLegId { get; set; }
    public OptionLeg OptionLeg { get; set; } = null!;
    public string BrokerOrderId { get; set; } = string.Empty;
    public OrderStatus Status { get; set; }
    public decimal FillPrice { get; set; }
    public decimal Slippage { get; set; }
    public DateTime PlacedAtUtc { get; set; } = DateTime.UtcNow;
}

public class NavSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public DateOnly AsOf { get; set; }
    public decimal Nav { get; set; }
    public decimal DailyPnl { get; set; }
    public decimal Charges { get; set; }
    public decimal MonthToDatePct { get; set; }
}

public class AuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string? HashPrev { get; set; }
}

public class TradeProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public Guid? PositionId { get; set; }
    public Position? Position { get; set; }
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public StrategyType Strategy { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public decimal IndiaVix { get; set; }
    public decimal AtmIv { get; set; }
    public decimal IvRank { get; set; }
    public decimal Score { get; set; }
    public decimal ExpectedReturnPct { get; set; }
    public string Rationale { get; set; } = string.Empty;
    public ProposalStatus Status { get; set; } = ProposalStatus.Proposed;
    public int EntryDte { get; set; }
    public int TargetExitDte { get; set; } = 21;
    public int Lots { get; set; }
    public int Qty { get; set; }
    public decimal NetCredit { get; set; }
    public decimal MaxLoss { get; set; }
    public decimal MarginBlocked { get; set; }
    public decimal MarginUtilPct { get; set; }
    // GTT + management tactic carried from the StrategyConfig so approval needs no config lookup.
    public bool GttEnabled { get; set; }
    public decimal GttPremiumPct { get; set; } = 200m;
    public decimal ProfitTargetPct { get; set; } = 50m;
    public decimal AdjustTriggerDelta { get; set; } = 0.30m;
    public ICollection<ProposalLeg> Legs { get; set; } = [];
}

public class ProposalLeg
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProposalId { get; set; }
    public TradeProposal Proposal { get; set; } = null!;
    public OptionType OptionType { get; set; }
    public Side Side { get; set; }
    public decimal Strike { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public string TradingSymbol { get; set; } = string.Empty;
    public long InstrumentToken { get; set; }
    public decimal MidPrice { get; set; }
    public decimal Delta { get; set; }
}

public class Adjustment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PositionId { get; set; }
    public Position Position { get; set; } = null!;
    public AdjustmentKind Kind { get; set; }
    public string TriggerReason { get; set; } = string.Empty;
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string? GreeksBeforeJson { get; set; }
    public string? GreeksAfterJson { get; set; }
    public bool Automated { get; set; }
}

public class VolatilitySnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public DateTime TakenAtUtc { get; set; } = DateTime.UtcNow;
    public decimal IndiaVix { get; set; }
    public decimal AtmIv { get; set; }
    public decimal IvRank { get; set; }
    public decimal IvPercentile { get; set; }
}

public class Alert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public string Kind { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime RaisedAtUtc { get; set; } = DateTime.UtcNow;
    public bool Acknowledged { get; set; }
}
