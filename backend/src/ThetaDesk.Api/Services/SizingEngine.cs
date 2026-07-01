using Microsoft.EntityFrameworkCore;
using ThetaDesk.Data;
using ThetaDesk.Api.Kite;
using ThetaDesk.Domain.Entities;
using ThetaDesk.Domain.Enums;

namespace ThetaDesk.Api.Services;

public record LimitVerdict(bool Passed, IReadOnlyList<string> Violations);

public class SizingEngine(ThetaDeskDbContext db, IKiteClient kite)
{
    private static readonly PositionStatus[] OpenStatuses =
        [PositionStatus.Open, PositionStatus.AutoAdjusting, PositionStatus.ProfitTaking, PositionStatus.RiskStopping, PositionStatus.PendingFill];

    public async Task<(LimitVerdict Verdict, decimal MarginBlocked, decimal MarginUtilPct)>
        ValidateProposalAsync(TradeProposal proposal, Fund fund, CancellationToken ct = default)
    {
        var violations = new List<string>();

        if (proposal.MaxLoss > fund.PerPositionMaxLoss)
            violations.Add($"Max loss ₹{proposal.MaxLoss:N0} exceeds ₹{fund.PerPositionMaxLoss:N0} per-position cap");

        var basketLegs = proposal.Legs.Select(l => new BasketLeg(
            l.TradingSymbol, "NFO",
            l.Side == Side.Buy ? "BUY" : "SELL",
            proposal.Qty, "NRML", "MARKET")).ToList();

        KiteMargin margin;
        try
        {
            margin = await kite.GetBasketMarginAsync(basketLegs, ct);
        }
        catch
        {
            // If margin API unavailable, estimate conservatively
            margin = new KiteMargin(proposal.MaxLoss * 3, proposal.MaxLoss, proposal.MaxLoss * 3);
        }

        // Cumulative utilisation: margin already blocked by open positions + this new basket.
        // DB-sourced so it stays consistent in both live and paper modes.
        decimal usedMargin = await db.Positions
            .Where(p => p.FundId == fund.Id && OpenStatuses.Contains(p.Status))
            .SumAsync(p => p.MarginBlocked, ct);
        decimal capital = fund.CashBalance > 0 ? fund.CashBalance : await kite.GetFundsAsync(ct);
        decimal projectedUtilPct = capital > 0 ? (usedMargin + margin.Total) / capital * 100 : 100;

        if (projectedUtilPct > fund.MaxMarginUtilPct)
            violations.Add($"Projected margin utilisation {projectedUtilPct:F1}% would exceed {fund.MaxMarginUtilPct}% cap "
                         + $"(₹{usedMargin:N0} already deployed)");

        return (new LimitVerdict(violations.Count == 0, violations), margin.Total, projectedUtilPct);
    }
}
