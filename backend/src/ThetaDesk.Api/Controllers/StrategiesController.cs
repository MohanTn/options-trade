using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ThetaDesk.Data;
using ThetaDesk.Domain.Entities;
using ThetaDesk.Domain.Enums;

namespace ThetaDesk.Api.Controllers;

[ApiController]
[Route("api/v1/strategies")]
[Authorize]
public class StrategiesController(ThetaDeskDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var configs = await db.StrategyConfigs.Include(s => s.Legs)
            .OrderBy(s => s.VixMin)
            .ToListAsync(ct);
        return Ok(configs.Select(Map));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StrategyConfigDto dto, CancellationToken ct)
    {
        var fund = await db.Funds.FirstOrDefaultAsync(ct);
        if (fund == null) return NotFound(new { error = "Fund not configured" });

        if (Validate(dto) is { } error) return BadRequest(new { error });

        var config = new StrategyConfig { FundId = fund.Id };
        Apply(config, dto);
        db.StrategyConfigs.Add(config);
        await db.SaveChangesAsync(ct);
        return Created($"/api/v1/strategies/{config.Id}", Map(config));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] StrategyConfigDto dto, CancellationToken ct)
    {
        var config = await db.StrategyConfigs.Include(s => s.Legs).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (config == null) return NotFound();

        if (Validate(dto) is { } error) return BadRequest(new { error });

        // Remove old legs explicitly so EF marks them Deleted without touching the navigation property.
        db.StrategyLegs.RemoveRange(config.Legs);

        // Apply scalar fields only (no Legs assignment, which causes EF to emit UPDATE instead of INSERT).
        ApplyScalars(config, dto);

        // Add new legs directly to the DbSet — db.Add always marks entities as Added regardless of key value.
        var newLegs = BuildLegs(id, dto);
        db.StrategyLegs.AddRange(newLegs);

        await db.SaveChangesAsync(ct);
        config.Legs = newLegs; // populate for Map after save
        return Ok(Map(config));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var config = await db.StrategyConfigs.Include(s => s.Legs).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (config == null) return NotFound();
        db.StrategyConfigs.Remove(config); // EF cascade-marks legs as Deleted
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string? Validate(StrategyConfigDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return "Name is required";
        if (!Enum.TryParse<StrategyType>(dto.Strategy, out _)) return $"Unknown strategy '{dto.Strategy}'";
        if (dto.VixMax <= dto.VixMin) return "VixMax must be greater than VixMin";
        if (dto.EntryDteMax < dto.EntryDteMin) return "EntryDteMax must be ≥ EntryDteMin";
        if (dto.SizingPct is <= 0 or > 100) return "SizingPct must be in (0, 100]";
        if (dto.GttEnabled && dto.GttPremiumPct <= 0) return "GttPremiumPct must be positive when GTT is enabled";
        if (dto.Legs.Count == 0) return "At least one leg is required";
        foreach (var l in dto.Legs)
        {
            if (l.OptionType is not ("CE" or "PE")) return $"Leg optionType must be CE or PE (got '{l.OptionType}')";
            if (l.Side is not ("Buy" or "Sell")) return $"Leg side must be Buy or Sell (got '{l.Side}')";
            if (l.Expiry is not ("Near" or "Far")) return $"Leg expiry must be Near or Far (got '{l.Expiry}')";
            if (l.TargetDelta is < 0 or > 1) return "Leg targetDelta must be between 0 and 1";
        }
        return null;
    }

    private static void Apply(StrategyConfig config, StrategyConfigDto dto)
    {
        ApplyScalars(config, dto);
        config.Legs = BuildLegs(config.Id, dto);
    }

    private static void ApplyScalars(StrategyConfig config, StrategyConfigDto dto)
    {
        config.Name = dto.Name.Trim();
        config.Enabled = dto.Enabled;
        config.Strategy = Enum.Parse<StrategyType>(dto.Strategy);
        config.VixMin = dto.VixMin;
        config.VixMax = dto.VixMax;
        config.EntryDteMin = dto.EntryDteMin;
        config.EntryDteMax = dto.EntryDteMax;
        config.SizingPct = dto.SizingPct;
        config.WeeklyCompounding = dto.WeeklyCompounding;
        config.GttEnabled = dto.GttEnabled;
        config.GttPremiumPct = dto.GttPremiumPct;
        config.ProfitTargetPct = dto.ProfitTargetPct;
        config.TargetExitDte = dto.TargetExitDte;
        config.AdjustTriggerDelta = dto.AdjustTriggerDelta;
    }

    private static List<StrategyLeg> BuildLegs(Guid configId, StrategyConfigDto dto) =>
        dto.Legs.Select(l => new StrategyLeg
        {
            StrategyConfigId = configId,
            OptionType = Enum.Parse<OptionType>(l.OptionType),
            Side = Enum.Parse<Side>(l.Side),
            TargetDelta = l.TargetDelta,
            Expiry = Enum.Parse<ExpiryRank>(l.Expiry),
        }).ToList();

    private static object Map(StrategyConfig s) => new
    {
        s.Id, s.Name, s.Enabled,
        strategy = s.Strategy.ToString(),
        s.VixMin, s.VixMax,
        s.EntryDteMin, s.EntryDteMax, s.SizingPct, s.WeeklyCompounding,
        s.GttEnabled, s.GttPremiumPct,
        s.ProfitTargetPct, s.TargetExitDte, s.AdjustTriggerDelta,
        legs = s.Legs.Select(l => new
        {
            optionType = l.OptionType.ToString(),
            side = l.Side.ToString(),
            l.TargetDelta,
            expiry = l.Expiry.ToString()
        })
    };
}

public record StrategyLegDto(string OptionType, string Side, decimal TargetDelta, string Expiry);
public record StrategyConfigDto(
    string Name, bool Enabled, string Strategy,
    decimal VixMin, decimal VixMax,
    int EntryDteMin, int EntryDteMax, decimal SizingPct, bool WeeklyCompounding,
    bool GttEnabled, decimal GttPremiumPct,
    decimal ProfitTargetPct, int TargetExitDte, decimal AdjustTriggerDelta,
    List<StrategyLegDto> Legs);
