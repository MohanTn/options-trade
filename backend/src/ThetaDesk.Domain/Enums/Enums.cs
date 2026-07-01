namespace ThetaDesk.Domain.Enums;

public enum StrategyType { ShortStrangle, IronCondor, DoubleCalendar, CreditSpread }
public enum PositionStatus { PendingFill, GttArmed, Open, AutoAdjusting, ProfitTaking, RiskStopping, ManualClosing, Closing, Closed, Settled }
public enum ProposalStatus { Proposed, Approved, Rejected, Expired }
public enum OptionType { CE, PE }
public enum Side { Buy, Sell }
public enum OrderStatus { Pending, Open, Complete, Cancelled, Rejected }
public enum LimitScope { Portfolio, Position }
public enum AdjustmentKind { RollUntestedSide, AddHedgeWing, ReduceLots, RecentreCalendar, Manual }
public enum AlertSeverity { Warning, Critical }
public enum VixRegime { LowCalendar, MidStrangle, HighCondor }
public enum ExpiryRank { Near, Far }
