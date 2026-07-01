using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ThetaDesk.Data;
using ThetaDesk.Api.Kite;
using ThetaDesk.Api.Services;
using ThetaDesk.Domain.Entities;
using ThetaDesk.Domain.Enums;

namespace ThetaDesk.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
[Authorize]
public class SystemController(
    IKiteClient kite,
    ThetaDeskDbContext db,
    SignalEngine signal,
    SizingEngine sizing,
    AuditService audit,
    BrokerSyncService brokerSync,
    IConfiguration config,
    ILogger<SystemController> logger) : ControllerBase
{
    [HttpGet("session")]
    public async Task<IActionResult> GetSession()
    {
        var status = await kite.GetSessionStatusAsync();
        var paperTrading = config.GetValue<bool>("Kite:PaperTrading");
        return Ok(new { status.Valid, status.LoginUrl, status.ExpiresAt, paperTrading });
    }

    [HttpPost("session")]
    public async Task<IActionResult> SetSession([FromBody] SessionRequest req, CancellationToken ct)
    {
        try
        {
            await kite.ExchangeTokenAsync(req.RequestToken, ct);
            var status = await kite.GetSessionStatusAsync();

            // Auto-fetch capital from broker and persist (non-fatal)
            decimal? totalCapital = null;
            try
            {
                totalCapital = await kite.GetFundsAsync(ct);
                var fund = await db.Funds.FirstOrDefaultAsync(ct);
                if (fund != null)
                {
                    fund.CashBalance = totalCapital.Value;
                    fund.CurrentNav = totalCapital.Value;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("Fund capital updated from broker: {Capital}", totalCapital);
                }
            }
            catch (Exception fundEx)
            {
                logger.LogWarning(fundEx, "Failed to fetch capital from broker after token exchange");
            }

            // Pull in any positions held at the broker but not tracked locally (e.g. a double
            // calendar placed directly in Kite) so the desk reflects reality on session start.
            int ingested = 0;
            try
            {
                var fund = await db.Funds.FirstOrDefaultAsync(ct);
                if (fund != null)
                {
                    // Paper mode: seed the simulated ledger from real Kite holdings first, so the
                    // sync below ingests them into the DB and the paper book starts from reality.
                    if (kite is Kite.PaperKiteClient paper)
                        await paper.SeedFromBrokerAsync(ct);

                    ingested = await brokerSync.SyncAsync(fund, ct);
                    if (ingested > 0)
                        await audit.LogAsync(fund.Id, "System", "BrokerPositionsIngested", null, new { count = ingested }, ct);
                }
            }
            catch (Exception syncEx)
            {
                logger.LogWarning(syncEx, "Broker position sync on session start failed");
            }

            return Ok(new { status.Valid, status.ExpiresAt, totalCapital, ingestedPositions = ingested });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Kite token exchange failed");
            return StatusCode(502, new { error = "Broker token exchange failed", detail = ex.Message });
        }
    }

    [HttpDelete("session")]
    public async Task<IActionResult> ClearSession(CancellationToken ct)
    {
        await kite.ClearSessionAsync(ct);
        return NoContent();
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        var session = await kite.GetSessionStatusAsync();
        if (!session.Valid)
            return Conflict(new { error = "Kite session not valid. Refresh the session token first." });

        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        if (fund == null)
            return NotFound(new { error = "Fund not configured" });

        List<TradeProposal> candidates;
        string rejectReason;
        try
        {
            (candidates, rejectReason) = await signal.GenerateCandidatesAsync(fund, maxCandidates: 5, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ProposalGenFailed");
            return StatusCode(502, new { error = "Signal engine failed", detail = ex.Message });
        }

        if (candidates.Count == 0)
            return Ok(new { noCandidate = true, rejectReason }); // 200 with reason so frontend can display it

        // Supersede any earlier still-open proposals — only the latest scan's batch is live for approval.
        var stale = await db.Proposals
            .Where(p => p.FundId == fund.Id && p.Status == ProposalStatus.Proposed)
            .ToListAsync(ct);
        foreach (var p in stale) p.Status = ProposalStatus.Expired;

        // Persist every candidate (even ones that breach capital limits) so the operator can compare;
        // the per-candidate verdict drives whether its Approve button is enabled in the UI.
        var generatedAt = DateTime.UtcNow;
        var mapped = new List<object>();
        foreach (var c in candidates)
        {
            var (verdict, marginBlocked, marginUtilPct) = await sizing.ValidateProposalAsync(c, fund, ct);
            c.MarginBlocked = marginBlocked;
            c.MarginUtilPct = marginUtilPct;
            c.GeneratedAtUtc = generatedAt;
            db.Proposals.Add(c);
            mapped.Add(MapProposal(c, verdict));
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(fund.Id, "System", "ProposalsGenerated", null,
            new { count = candidates.Count, topScore = candidates[0].Score, topAtmIv = candidates[0].AtmIv }, ct);

        return Ok(new { candidates = mapped });
    }

    [HttpPost("kill-switch")]
    public IActionResult KillSwitch([FromBody] KillSwitchRequest req, [FromServices] KillSwitchState state)
    {
        state.Enabled = req.Enable;
        logger.LogWarning("Kill-switch set to {State} by operator", req.Enable ? "ON" : "OFF");
        return Ok(new { killSwitchEnabled = state.Enabled });
    }

    private static object MapProposal(TradeProposal p, LimitVerdict v) => new
    {
        proposalId = p.Id,
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
        legs = p.Legs.Select(l => new { l.OptionType, l.Side, l.Strike, l.TradingSymbol, l.MidPrice }),
        limitVerdict = new { v.Passed, v.Violations }
    };
}

public record SessionRequest(string RequestToken);
public record KillSwitchRequest(bool Enable);

public class KillSwitchState
{
    public bool Enabled { get; set; }
}
