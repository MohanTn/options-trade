using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ThetaDesk.Data;
using ThetaDesk.Api.Services;
using ThetaDesk.Domain.Entities;
using ThetaDesk.Domain.Enums;

namespace ThetaDesk.Api.Controllers;

[ApiController]
[Route("api/v1/proposals")]
[Authorize]
public class ProposalsController(
    ThetaDeskDbContext db,
    SizingEngine sizing,
    AuditService audit) : ControllerBase
{
    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent(CancellationToken ct)
    {
        var proposal = await db.Proposals
            .Include(p => p.Legs)
            .Where(p => p.Status == ProposalStatus.Proposed)
            .OrderByDescending(p => p.GeneratedAtUtc).ThenByDescending(p => p.Score)
            .FirstOrDefaultAsync(ct);

        // The header card only reads VIX/IV/DTE here, so skip the broker margin round-trip a verdict needs.
        if (proposal == null) return NoContent();
        return Ok(MapProposal(proposal));
    }

    /// <summary>The current batch of suggested candidates (highest score first) for the operator to choose from.</summary>
    [HttpGet("candidates")]
    public async Task<IActionResult> GetCandidates(CancellationToken ct)
    {
        var props = await db.Proposals
            .Include(p => p.Legs)
            .Where(p => p.Status == ProposalStatus.Proposed)
            .OrderByDescending(p => p.GeneratedAtUtc).ThenByDescending(p => p.Score)
            .ToListAsync(ct);
        if (props.Count == 0) return Ok(Array.Empty<object>());

        // Re-validate against the latest prices so each card shows a live capital-limit verdict.
        var result = new List<object>(props.Count);
        foreach (var p in props)
        {
            var fund = await db.Funds.FindAsync([p.FundId], ct); // EF identity-map caches repeat lookups
            if (fund == null) { result.Add(MapProposal(p)); continue; }
            var (verdict, _, _) = await sizing.ValidateProposalAsync(p, fund, ct);
            result.Add(MapProposal(p, verdict));
        }
        return Ok(result);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var proposal = await db.Proposals.Include(p => p.Legs).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (proposal == null) return NotFound();
        if (proposal.Status != ProposalStatus.Proposed)
            return Conflict(new { error = "Proposal is not in Proposed state", status = proposal.Status.ToString() });

        var fund = await db.Funds.FindAsync([proposal.FundId], ct);
        if (fund == null) return NotFound(new { error = "Fund not found" });

        // Re-validate limits on latest prices
        var (verdict, marginBlocked, utilPct) = await sizing.ValidateProposalAsync(proposal, fund, ct);
        if (!verdict.Passed)
            return UnprocessableEntity(new { error = "Re-validation failed", violations = verdict.Violations });

        // Order placement is done manually by the operator at the broker. Zerodha requires a fixed
        // source IP for order placement, so the desk no longer submits the basket itself. We create
        // the position in PendingFill with each leg primed at its mid price as a suggested fill; the
        // operator places the legs at the broker, then confirms the actual fill prices via
        // POST /positions/{id}/confirm-entry. GTT stops are likewise placed manually by the operator.
        var legList = proposal.Legs.ToList();
        var position = new Position
        {
            FundId = fund.Id,
            ProposalId = proposal.Id,
            Strategy = proposal.Strategy,
            IsDefinedRisk = proposal.Strategy is StrategyType.IronCondor or StrategyType.DoubleCalendar,
            Status = PositionStatus.PendingFill,
            EntryDate = DateOnly.FromDateTime(DateTime.Today),
            ExpiryDate = proposal.ExpiryDate,
            EntryDte = proposal.EntryDte,
            TargetExitDte = proposal.TargetExitDte,
            NetCredit = proposal.NetCredit,
            MaxLoss = proposal.MaxLoss,
            MarginBlocked = marginBlocked,
            // Carry the management tactic from the proposal (sourced from its StrategyConfig).
            StopLossPremiumMult = proposal.GttPremiumPct / 100m,
            ProfitTakePct = proposal.ProfitTargetPct,
            AdjustTriggerDelta = proposal.AdjustTriggerDelta,
            Legs = legList.Select(l => new OptionLeg
            {
                InstrumentToken = l.InstrumentToken,
                OptionType = l.OptionType,
                Side = l.Side,
                Strike = l.Strike,
                Lots = proposal.Lots,
                Qty = proposal.Qty,
                EntryPrice = l.MidPrice, // mid as suggested fill; operator confirms the actual fill price
            }).ToList()
        };

        // Upsert instruments for each leg so the FK on OptionLegs.InstrumentToken is satisfied.
        // Instruments are normally seeded by BrokerSyncService; for paper/new trades they may be absent.
        var legTokens = legList.Select(l => l.InstrumentToken).ToHashSet();
        var existingTokens = (await db.Instruments
            .Where(i => legTokens.Contains(i.InstrumentToken))
            .Select(i => i.InstrumentToken)
            .ToListAsync(ct)).ToHashSet();

        if (existingTokens.Count < legTokens.Count)
        {
            foreach (var leg in legList.Where(l => !existingTokens.Contains(l.InstrumentToken)))
            {
                db.Instruments.Add(new Instrument
                {
                    InstrumentToken = leg.InstrumentToken,
                    TradingSymbol = leg.TradingSymbol,
                    Strike = leg.Strike,
                    ExpiryDate = leg.ExpiryDate,
                    OptionType = leg.OptionType,
                    LotSize = fund.LotSize,
                });
            }
        }

        proposal.Status = ProposalStatus.Approved;
        proposal.PositionId = position.Id;

        // The other candidates from the same scan are now obsolete — expire them so the chooser clears.
        var siblings = await db.Proposals
            .Where(p => p.FundId == fund.Id && p.Status == ProposalStatus.Proposed && p.Id != proposal.Id)
            .ToListAsync(ct);
        foreach (var s in siblings) s.Status = ProposalStatus.Expired;

        db.Positions.Add(position);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(fund.Id, "Operator", "ProposalApproved",
            new { proposal.Id }, new { position.Id }, ct);

        return Accepted(new
        {
            positionId = position.Id,
            status = position.Status.ToString()
        });
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectRequest? req, CancellationToken ct)
    {
        var proposal = await db.Proposals.FindAsync([id], ct);
        if (proposal == null) return NotFound();

        proposal.Status = ProposalStatus.Rejected;
        await db.SaveChangesAsync(ct);

        var fund = await db.Funds.FindAsync([proposal.FundId], ct);
        if (fund != null)
            await audit.LogAsync(fund.Id, "Operator", "ProposalRejected", new { proposal.Id }, new { reason = req?.Reason }, ct);

        return Ok(new { proposal.Id, status = proposal.Status.ToString() });
    }

    private static object MapProposal(TradeProposal p, LimitVerdict? verdict = null) => new
    {
        proposalId = p.Id,
        generatedAt = p.GeneratedAtUtc,
        strategy = p.Strategy.ToString(),
        expiry = p.ExpiryDate,
        entryDte = p.EntryDte,
        targetExitDte = p.TargetExitDte,
        indiaVix = p.IndiaVix,
        atmIv = p.AtmIv,
        ivRank = p.IvRank,
        score = p.Score,
        lots = p.Lots,
        qty = p.Qty,
        netCredit = p.NetCredit,
        maxLoss = p.MaxLoss,
        marginBlocked = p.MarginBlocked,
        marginUtilPct = p.MarginUtilPct,
        expectedReturnPct = p.ExpectedReturnPct,
        rationale = p.Rationale,
        status = p.Status.ToString(),
        legs = p.Legs.Select(l => new { l.OptionType, l.Side, l.Strike, l.TradingSymbol, l.MidPrice }),
        limitVerdict = verdict == null ? null : new { verdict.Passed, verdict.Violations }
    };
}

public record RejectRequest(string? Reason);
