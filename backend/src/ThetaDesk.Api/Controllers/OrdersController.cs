using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ThetaDesk.Data;

namespace ThetaDesk.Api.Controllers;

[ApiController]
[Route("api/v1/orders")]
[Authorize]
public class OrdersController(ThetaDeskDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        if (fund == null) return NotFound();

        // Position is referenced only for the fund filter, so it needs no Include — just the leg + instrument.
        var orders = await db.Orders
            .Include(o => o.OptionLeg).ThenInclude(l => l.Instrument)
            .Where(o => o.OptionLeg.Position.FundId == fund.Id)
            .OrderByDescending(o => o.PlacedAtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

        return Ok(orders.Select(o => new
        {
            o.Id,
            status = o.Status.ToString(),
            o.FillPrice, o.Slippage, o.PlacedAtUtc,
            side = o.OptionLeg.Side.ToString(),
            o.OptionLeg.Qty,
            // Instrument is upserted before any leg, so it is always present; fall back to the token defensively.
            tradingSymbol = o.OptionLeg.Instrument != null ? o.OptionLeg.Instrument.TradingSymbol : o.OptionLeg.InstrumentToken.ToString()
        }));
    }
}
