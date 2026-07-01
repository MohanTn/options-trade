using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ThetaDesk.Data;
using ThetaDesk.Api.Kite;
using ThetaDesk.Domain.Enums;
using ThetaDesk.Greeks;

namespace ThetaDesk.Api.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize]
public class PortfolioController(ThetaDeskDbContext db, IKiteClient kite, ILogger<PortfolioController> logger) : ControllerBase
{
    private const double RiskFreeRate = 0.065;

    [HttpGet("portfolio/greeks")]
    public async Task<IActionResult> GetGreeks(CancellationToken ct)
    {
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        if (fund == null) return NotFound();

        // Aggregate the latest greeks snapshot per open position
        var openPositions = await db.Positions
            .Include(p => p.Legs)
            .Where(p => p.Status == PositionStatus.Open || p.Status == PositionStatus.AutoAdjusting)
            .ToListAsync(ct);

        var positionIds = openPositions.Select(p => p.Id).ToList();
        var latestSnapshots = await db.GreeksSnapshots
            .Where(g => positionIds.Contains(g.PositionId))
            .GroupBy(g => g.PositionId)
            .Select(g => g.OrderByDescending(x => x.TakenAtUtc).First())
            .ToListAsync(ct);

        double netDelta = latestSnapshots.Sum(g => (double)g.Delta);
        double netGamma = latestSnapshots.Sum(g => (double)g.Gamma);
        double netTheta = latestSnapshots.Sum(g => (double)g.Theta);
        double netVega = latestSnapshots.Sum(g => (double)g.Vega);

        var totalUnrealised = openPositions.Sum(p => p.UnrealisedPnl);
        var openCount = openPositions.Count;

        // Compute greeks from live broker positions
        var session = await kite.GetSessionStatusAsync();
        if (session.Valid)
        {
            try
            {
                var brokerPositions = await kite.GetPositionsAsync(ct);
                var nfoOptions = brokerPositions.Where(p => p.Exchange == "NFO").ToList();

                if (nfoOptions.Count > 0)
                {
                    // Tokens already tracked in DB — their P&L is kept live by the positions sync
                    var trackedTokens = openPositions
                        .SelectMany(p => p.Legs.Select(l => l.InstrumentToken))
                        .ToHashSet();

                    var forwardPrice = (double)await kite.GetNiftySpotAsync(ct);

                    // Get instrument details to map tokens → strike/expiry/type
                    var instruments = await kite.GetNiftyInstrumentsAsync(ct);
                    var instrumentMap = instruments.ToDictionary(i => i.Token);

                    // Get quotes for IV
                    var tokens = nfoOptions.Select(p => p.InstrumentToken).ToList();
                    var quotes = await kite.GetQuotesAsync(tokens, ct);
                    var quoteMap = quotes.ToDictionary(q => q.Token);

                    var today = DateOnly.FromDateTime(DateTime.UtcNow);

                    foreach (var pos in nfoOptions)
                    {
                        if (!instrumentMap.TryGetValue(pos.InstrumentToken, out var inst)) continue;

                        var quote = quoteMap.GetValueOrDefault(pos.InstrumentToken);
                        double iv = (double)(quote?.Iv ?? 0);
                        double ltp = (double)(quote?.Ltp ?? pos.LastPrice);

                        var daysToExpiry = inst.Expiry.DayNumber - today.DayNumber;
                        double t = Math.Max(daysToExpiry / 365.0, 0);

                        if (iv <= 0 && ltp > 0 && t > 0)
                            iv = Black76.SolveIv(ltp, forwardPrice, (double)inst.Strike, t, RiskFreeRate, inst.OptionType == "CE");

                        var leg = new LegGreeksInput(
                            pos.TradingSymbol,
                            IsCall: inst.OptionType == "CE",
                            IsBuy: pos.Quantity > 0,
                            Strike: (double)inst.Strike,
                            ForwardPrice: forwardPrice,
                            TimeToExpiryYears: t,
                            RiskFreeRate: RiskFreeRate,
                            ImpliedVol: iv,
                            Qty: Math.Abs(pos.Quantity));

                        var (g, _) = GreeksAggregator.ComputeLeg(leg);
                        netDelta += g.Delta;
                        netGamma += g.Gamma;
                        netTheta += g.Theta;
                        netVega += g.Vega;
                    }

                    // Only count/tally P&L for positions not already in DB (tracked ones are in openPositions)
                    var untrackedBroker = nfoOptions.Where(p => !trackedTokens.Contains(p.InstrumentToken)).ToList();
                    openCount += untrackedBroker.Count;
                    totalUnrealised += untrackedBroker.Sum(p => p.UnrealisedPnl);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to compute greeks from broker positions");
            }
        }

        var startingNav = fund.StartingCapital;
        var drawdownPct = startingNav > 0 ? (startingNav - fund.CurrentNav) / startingNav * 100 : 0;

        // Margin utilization from live broker data
        decimal usedMargin = 0;
        decimal totalCapital = fund.CashBalance;
        decimal availableBalance = fund.CashBalance;
        decimal marginSpan = 0, marginExposure = 0, optionPremium = 0, collateral = 0;
        if (session.Valid)
        {
            try
            {
                var margin = await kite.GetMarginSummaryAsync(ct);
                totalCapital = margin.TotalCapital;
                usedMargin = margin.UsedMargin;
                availableBalance = margin.AvailableBalance;
                marginSpan = margin.Span;
                marginExposure = margin.Exposure;
                optionPremium = margin.OptionPremium;
                collateral = margin.Collateral;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch margin summary from broker");
            }
        }
        var marginUtilPct = totalCapital > 0 ? usedMargin / totalCapital * 100 : 0;

        return Ok(new
        {
            netDelta,
            netGamma,
            netTheta,
            netVega,
            marginUtilPct,
            usedMargin,
            availableBalance,
            totalCapital,
            marginBreakdown = new { span = marginSpan, exposure = marginExposure, optionPremium, collateral },
            unrealisedPnl = totalUnrealised,
            drawdownPct,
            currentNav = fund.CurrentNav,
            openPositionCount = openCount
        });
    }

    [HttpGet("market/ticks")]
    public async Task<IActionResult> GetMarketTicks(CancellationToken ct)
    {
        var session = await kite.GetSessionStatusAsync();
        if (!session.Valid) return Ok(new { nifty = (decimal?)null, vix = (decimal?)null });
        try
        {
            var results = await Task.WhenAll(kite.GetNiftySpotAsync(ct), kite.GetIndiaVixAsync(ct));
            var nifty = results[0];
            var vix = results[1];
            return Ok(new { nifty, vix });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch market ticks");
            return Ok(new { nifty = (decimal?)null, vix = (decimal?)null });
        }
    }

    [HttpGet("risk/limits")]
    public async Task<IActionResult> GetLimits(CancellationToken ct)
    {
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        if (fund == null) return NotFound();
        var limits = await db.RiskLimits.Where(l => l.FundId == fund.Id).ToListAsync(ct);
        return Ok(limits.Select(l => new
        {
            l.Id, scope = l.Scope.ToString(), l.Metric, l.LowerBound, l.UpperBound, l.Hard
        }));
    }

    [HttpPut("risk/limits/{id:guid}")]
    public async Task<IActionResult> UpdateLimit(Guid id, [FromBody] UpdateLimitRequest req, CancellationToken ct)
    {
        var limit = await db.RiskLimits.FindAsync([id], ct);
        if (limit == null) return NotFound();
        limit.LowerBound = req.LowerBound;
        limit.UpperBound = req.UpperBound;
        limit.Hard = req.Hard;
        await db.SaveChangesAsync(ct);
        return Ok(new { limit.Id, limit.Metric, limit.LowerBound, limit.UpperBound, limit.Hard });
    }

    [HttpGet("alerts")]
    public async Task<IActionResult> GetAlerts([FromQuery] bool? ack, CancellationToken ct)
    {
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        if (fund == null) return NotFound();

        var query = db.Alerts.Where(a => a.FundId == fund.Id);
        if (ack.HasValue) query = query.Where(a => a.Acknowledged == ack.Value);
        var alerts = await query.OrderByDescending(a => a.RaisedAtUtc).Take(100).ToListAsync(ct);

        return Ok(alerts.Select(a => new
        {
            a.Id, a.Kind, severity = a.Severity.ToString(), a.Message, a.RaisedAtUtc, a.Acknowledged
        }));
    }

    [HttpPost("alerts/{id:guid}/ack")]
    public async Task<IActionResult> AckAlert(Guid id, CancellationToken ct)
    {
        var alert = await db.Alerts.FindAsync([id], ct);
        if (alert == null) return NotFound();
        alert.Acknowledged = true;
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        if (fund == null) return NotFound();
        var entries = await db.AuditLog
            .Where(a => a.FundId == fund.Id)
            .OrderByDescending(a => a.AtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
        return Ok(entries.Select(a => new
        {
            a.Id, a.Actor, a.Action,
            a.BeforeJson, a.AfterJson, a.AtUtc, a.HashPrev
        }));
    }

    [HttpGet("fund")]
    public async Task<IActionResult> GetFund(CancellationToken ct)
    {
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        if (fund == null) return NotFound();
        return Ok(MapFund(fund));
    }

    [HttpPut("fund")]
    public async Task<IActionResult> UpdateFund([FromBody] UpdateFundRequest req, CancellationToken ct)
    {
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        if (fund == null) return NotFound();

        if (ValidateFund(req) is { } error) return BadRequest(new { error });

        fund.Name = req.Name.Trim();
        fund.StartingCapital = req.StartingCapital;
        fund.CashBalance = req.CashBalance;
        fund.MonthlyTargetPct = req.MonthlyTargetPct;
        fund.MaxMarginUtilPct = req.MaxMarginUtilPct;
        fund.PerPositionMaxLoss = req.PerPositionMaxLoss;
        fund.DrawdownStopPct = req.DrawdownStopPct;
        fund.ProfitTakePct = req.ProfitTakePct;
        fund.LotSize = req.LotSize;
        await db.SaveChangesAsync(ct);

        return Ok(MapFund(fund));
    }

    // CurrentNav is mark-to-market state synced from the broker, not an operator-editable setting.
    private static string? ValidateFund(UpdateFundRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Name is required";
        if (r.StartingCapital <= 0) return "Starting capital must be positive";
        if (r.CashBalance < 0) return "Cash balance cannot be negative";
        if (r.MonthlyTargetPct < 0) return "Monthly target cannot be negative";
        if (r.MaxMarginUtilPct is <= 0 or > 100) return "Max margin utilisation must be in (0, 100]";
        if (r.PerPositionMaxLoss <= 0) return "Per-position max loss must be positive";
        if (r.DrawdownStopPct is <= 0 or > 100) return "Drawdown stop must be in (0, 100]";
        if (r.ProfitTakePct is <= 0 or > 100) return "Profit-take must be in (0, 100]";
        if (r.LotSize <= 0) return "Lot size must be positive";
        return null;
    }

    private static object MapFund(Domain.Entities.Fund fund) => new
    {
        fund.Id, fund.Name,
        fund.StartingCapital, fund.CashBalance, fund.CurrentNav,
        fund.MonthlyTargetPct, fund.MaxMarginUtilPct,
        fund.PerPositionMaxLoss, fund.DrawdownStopPct, fund.ProfitTakePct,
        fund.LotSize
    };

    [HttpGet("nav/history")]
    public async Task<IActionResult> GetNavHistory([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        if (fund == null) return NotFound();

        var query = db.NavSnapshots.Where(n => n.FundId == fund.Id);
        if (from.HasValue) query = query.Where(n => n.AsOf >= from.Value);
        if (to.HasValue) query = query.Where(n => n.AsOf <= to.Value);

        var snapshots = await query.OrderBy(n => n.AsOf).ToListAsync(ct);
        return Ok(new
        {
            fundName = fund.Name,
            startingCapital = fund.StartingCapital,
            currentNav = fund.CurrentNav,
            snapshots = snapshots.Select(s => new
            {
                s.AsOf, s.Nav, s.DailyPnl, s.Charges, s.MonthToDatePct
            })
        });
    }
}

public record UpdateLimitRequest(decimal? LowerBound, decimal? UpperBound, bool Hard);
public record UpdateFundRequest(
    string Name, decimal StartingCapital, decimal CashBalance,
    decimal MonthlyTargetPct, decimal MaxMarginUtilPct,
    decimal PerPositionMaxLoss, decimal DrawdownStopPct, decimal ProfitTakePct,
    int LotSize);
