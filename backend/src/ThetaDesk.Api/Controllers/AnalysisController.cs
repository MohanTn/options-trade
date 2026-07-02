using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ThetaDesk.Api.Services;

namespace ThetaDesk.Api.Controllers;

[ApiController]
[Route("api/v1/analysis")]
[Authorize]
public class AnalysisController(ChainAnalysisService chainAnalysis, ILogger<AnalysisController> logger) : ControllerBase
{
    /// <summary>
    /// Near-week + near-month option-chain read: PCR, max pain, OI support/resistance,
    /// IV skew/term structure and the composed directional bias. Served from a short-lived
    /// cache unless <paramref name="refresh"/> forces a fresh broker scan.
    /// </summary>
    /// <summary>Today's intraday vega-flow series for the chart — a pure Redis read.</summary>
    [HttpGet("vega-flow")]
    public async Task<IActionResult> VegaFlow() => Ok(await chainAnalysis.GetVegaFlowSeriesAsync());

    /// <summary>Trading dates with a permanent EOD vega-flow snapshot in Postgres, most recent first.</summary>
    [HttpGet("vega-flow/history-dates")]
    public async Task<IActionResult> VegaFlowHistoryDates(CancellationToken ct) =>
        Ok(await chainAnalysis.GetVegaFlowSnapshotDatesAsync(ct));

    /// <summary>One trading day's archived vega-flow series (404 if that day has no snapshot yet — it's written at market close).</summary>
    [HttpGet("vega-flow/history/{date}")]
    public async Task<IActionResult> VegaFlowHistory(DateOnly date, CancellationToken ct)
    {
        var points = await chainAnalysis.GetVegaFlowSnapshotAsync(date, ct);
        return points == null ? NotFound() : Ok(points);
    }

    [HttpGet("chain")]
    public async Task<IActionResult> Chain([FromQuery] bool refresh = false, CancellationToken ct = default)
    {
        try
        {
            return Ok(await chainAnalysis.AnalyzeAsync(refresh, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Chain analysis failed");
            return StatusCode(502, new { error = $"Chain analysis unavailable: {ex.Message}. Is the Kite session connected?" });
        }
    }
}
