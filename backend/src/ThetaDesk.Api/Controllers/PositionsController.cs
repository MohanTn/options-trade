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
[Route("api/v1/positions")]
[Authorize]
public class PositionsController(ThetaDeskDbContext db, IKiteClient kite, BrokerSyncService brokerSync, AuditService audit) : ControllerBase
{
    [HttpGet("broker")]
    public async Task<IActionResult> BrokerPositions(CancellationToken ct)
    {
        var session = await kite.GetSessionStatusAsync();
        if (!session.Valid)
            return Conflict(new { error = "Kite session not valid" });

        var positions = await kite.GetPositionsAsync(ct);
        return Ok(positions.Select(p => new
        {
            p.TradingSymbol, p.Exchange, p.Product,
            p.Quantity, p.AveragePrice, p.LastPrice,
            p.UnrealisedPnl, p.RealisedPnl
        }));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct)
    {
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        bool sessionValid = false;
        if (fund != null)
        {
            var session = await kite.GetSessionStatusAsync();
            sessionValid = session.Valid;
            if (session.Valid)
            {
                try { await brokerSync.SyncAsync(fund, ct); }
                catch (Exception ex) { _ = ex; }
                finally { db.ChangeTracker.Clear(); } // reset so the list query always reads from DB, not dirty tracker
            }
        }

        var query = db.Positions.Include(p => p.Legs).ThenInclude(l => l.Instrument).AsQueryable();
        if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            query = query.Where(p => p.Status != PositionStatus.Closed && p.Status != PositionStatus.Settled);
        else if (Enum.TryParse<PositionStatus>(status, out var s))
            query = query.Where(p => p.Status == s);

        var positions = await query.OrderByDescending(p => p.EntryDate).ToListAsync(ct);

        // Reprice open position legs from live quotes so P&L auto-updates every poll,
        // independent of the broker positions API (which can fail silently or miss NRML holdings).
        if (sessionValid)
        {
            try
            {
                var activePositions = positions
                    .Where(p => p.Status is not PositionStatus.Closed and not PositionStatus.Settled)
                    .ToList();
                var tokens = activePositions
                    .SelectMany(p => p.Legs)
                    .Select(l => l.InstrumentToken)
                    .Where(t => t != 0)
                    .Distinct()
                    .ToList();
                if (tokens.Count > 0)
                {
                    var quotes = (await kite.GetQuotesAsync(tokens, ct)).ToDictionary(q => q.Token);
                    foreach (var pos in activePositions)
                    {
                        var changed = false;
                        foreach (var leg in pos.Legs)
                        {
                            if (quotes.TryGetValue(leg.InstrumentToken, out var q) && q.Ltp > 0)
                            {
                                leg.CurrentPrice = q.Ltp;
                                changed = true;
                            }
                        }
                        if (changed) pos.UnrealisedPnl = ComputePnl(pos);
                    }
                    await db.SaveChangesAsync(ct);
                }
            }
            catch { }
        }

        return Ok(positions.Select(p => new
        {
            p.Id, p.FundId,
            strategy = p.Strategy.ToString(),
            status = p.Status.ToString(),
            p.EntryDate, p.ExpiryDate, p.EntryDte,
            p.NetCredit, p.MaxLoss, p.MarginBlocked,
            p.RealisedPnl, p.UnrealisedPnl,
            p.GttStopOrderId, p.StopLossPremiumMult,
            legs = p.Legs.Select(l => new
            {
                l.Id,
                l.Strike,
                optionType = l.OptionType.ToString(),
                side = l.Side.ToString(),
                l.Lots, l.Qty, l.EntryPrice, l.CurrentPrice,
                expiryDate = l.Instrument.ExpiryDate
            })
        }));
    }

    [HttpGet("{id:guid}/adjustments")]
    public async Task<IActionResult> GetAdjustments(Guid id, CancellationToken ct)
    {
        var adjustments = await db.Adjustments
            .Where(a => a.PositionId == id)
            .OrderByDescending(a => a.AtUtc)
            .ToListAsync(ct);

        return Ok(adjustments.Select(a => new
        {
            a.Id, kind = a.Kind.ToString(), a.TriggerReason, a.AtUtc, a.Automated,
            a.GreeksBeforeJson, a.GreeksAfterJson
        }));
    }

    /// <summary>
    /// Confirm the actual broker fill prices for a PendingFill position. The operator places the
    /// basket manually at the broker (Zerodha needs a fixed IP for order placement) and reports the
    /// fills here — this sets each leg's entry price, records the fill as an order, and opens the position.
    /// </summary>
    [HttpPost("{id:guid}/confirm-entry")]
    public async Task<IActionResult> ConfirmEntry(Guid id, [FromBody] LegPricesRequest req, CancellationToken ct)
    {
        var position = await db.Positions.Include(p => p.Legs).ThenInclude(l => l.Orders)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (position == null) return NotFound();
        if (position.Status != PositionStatus.PendingFill)
            return Conflict(new { error = "Position is not awaiting fills", status = position.Status.ToString() });

        var prices = (req.Legs ?? []).ToDictionary(l => l.LegId);
        foreach (var leg in position.Legs)
        {
            if (!prices.TryGetValue(leg.Id, out var p) || p.EntryPrice is not { } entry || entry <= 0)
                return BadRequest(new { error = $"Missing or invalid fill price for leg {leg.Id}" });

            var mid = leg.EntryPrice; // placeholder mid carried from the proposal
            leg.EntryPrice = entry;
            leg.Orders.Add(new Order
            {
                BrokerOrderId = "MANUAL",
                Status = OrderStatus.Complete,
                FillPrice = entry,
                Slippage = Math.Abs(entry - mid),
                PlacedAtUtc = DateTime.UtcNow,
            });
        }

        position.NetCredit = ComputeNetCredit(position);
        position.Status = PositionStatus.Open;
        await db.SaveChangesAsync(ct);

        await LogAsync(position, "EntryConfirmed", new { netCredit = position.NetCredit }, ct);
        return Ok(new { position.Id, status = position.Status.ToString(), position.NetCredit });
    }

    /// <summary>Edit the entry and/or exit (current) prices of a position's legs and recompute P&amp;L.</summary>
    [HttpPut("{id:guid}/legs")]
    public async Task<IActionResult> EditLegs(Guid id, [FromBody] LegPricesRequest req, CancellationToken ct)
    {
        var position = await db.Positions.Include(p => p.Legs).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (position == null) return NotFound();

        var prices = (req.Legs ?? []).ToDictionary(l => l.LegId);
        foreach (var leg in position.Legs)
        {
            if (!prices.TryGetValue(leg.Id, out var p)) continue;
            if (p.EntryPrice is { } entry) leg.EntryPrice = entry;
            if (p.ExitPrice is { } exit) leg.CurrentPrice = exit;
        }

        position.NetCredit = ComputeNetCredit(position);
        var pnl = ComputePnl(position);
        if (position.Status is PositionStatus.Closed or PositionStatus.Settled)
            position.RealisedPnl = pnl;
        else
            position.UnrealisedPnl = pnl;
        await db.SaveChangesAsync(ct);

        await LogAsync(position, "LegsEdited", new { position.NetCredit, pnl }, ct);
        return Ok(new { position.Id, position.NetCredit, position.RealisedPnl, position.UnrealisedPnl });
    }

    [HttpPost("{id:guid}/close")]
    public async Task<IActionResult> ManualClose(Guid id, [FromBody] CloseRequest req, CancellationToken ct)
    {
        var position = await db.Positions.Include(p => p.Legs).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (position == null) return NotFound();
        if (position.Status is PositionStatus.Closed or PositionStatus.Settled)
            return Conflict(new { error = "Position already closed" });

        // The operator closes the legs manually at the broker and reports the exit fills here.
        var exits = (req.Legs ?? []).ToDictionary(l => l.LegId);
        foreach (var leg in position.Legs)
            if (exits.TryGetValue(leg.Id, out var p) && p.ExitPrice is { } exit)
                leg.CurrentPrice = exit;

        position.RealisedPnl = ComputePnl(position);
        position.UnrealisedPnl = 0;
        position.Status = PositionStatus.Closed;
        await db.SaveChangesAsync(ct);

        await LogAsync(position, "ManualClose", new { reason = req.Reason, realisedPnl = position.RealisedPnl }, ct);
        return Accepted(new { position.Id, status = position.Status.ToString(), position.RealisedPnl });
    }

    // Net credit of a credit structure: premium received on sold legs less premium paid on bought legs.
    private static decimal ComputeNetCredit(Position p) =>
        p.Legs.Sum(l => (l.Side == Side.Sell ? 1 : -1) * l.EntryPrice * l.Qty);

    // Position P&L from entry vs current price; a short leg profits as its price falls. Legs without a
    // recorded current price (0) contribute nothing.
    private static decimal ComputePnl(Position p) =>
        p.Legs.Sum(l => l.CurrentPrice <= 0 ? 0
            : (l.Side == Side.Sell ? l.EntryPrice - l.CurrentPrice : l.CurrentPrice - l.EntryPrice) * l.Qty);

    private async Task LogAsync(Position position, string action, object after, CancellationToken ct)
    {
        var fund = await db.Funds.FindAsync([position.FundId], ct);
        if (fund != null)
            await audit.LogAsync(fund.Id, "Operator", action, new { position.Id }, after, ct);
    }

    [HttpPatch("{id:guid}/max-loss")]
    public async Task<IActionResult> UpdateMaxLoss(Guid id, [FromBody] UpdateMaxLossRequest req, CancellationToken ct)
    {
        if (req.MaxLoss <= 0) return BadRequest(new { error = "MaxLoss must be positive" });
        var position = await db.Positions.FindAsync([id], ct);
        if (position == null) return NotFound();
        position.MaxLoss = req.MaxLoss;
        await db.SaveChangesAsync(ct);
        return Ok(new { position.Id, position.MaxLoss });
    }

    [HttpPatch("{id:guid}/lots")]
    public async Task<IActionResult> UpdateLots(Guid id, [FromBody] UpdateLotsRequest req, CancellationToken ct)
    {
        if (req.Lots <= 0) return BadRequest(new { error = "Lots must be positive" });
        var position = await db.Positions.Include(p => p.Legs).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (position == null) return NotFound();
        var fund = await db.Funds.FindAsync([position.FundId], ct);
        if (fund == null) return NotFound();

        foreach (var leg in position.Legs)
        {
            leg.Lots = req.Lots;
            leg.Qty = req.Lots * fund.LotSize;
        }
        position.NetCredit = ComputeNetCredit(position);
        position.UnrealisedPnl = ComputePnl(position);
        await db.SaveChangesAsync(ct);

        await LogAsync(position, "LotsUpdated", new { lots = req.Lots, lotSize = fund.LotSize }, ct);
        return Ok(new { position.Id, lots = req.Lots, lotSize = fund.LotSize,
            position.NetCredit, position.UnrealisedPnl });
    }
}

public record LegPrice(Guid LegId, decimal? EntryPrice, decimal? ExitPrice);
public record LegPricesRequest(List<LegPrice>? Legs);
public record CloseRequest(string Reason, List<LegPrice>? Legs);
public record UpdateMaxLossRequest(decimal MaxLoss);
public record UpdateLotsRequest(int Lots);
