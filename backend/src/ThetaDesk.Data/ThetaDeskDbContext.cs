using Microsoft.EntityFrameworkCore;
using ThetaDesk.Domain.Entities;
using ThetaDesk.Domain.Enums;

namespace ThetaDesk.Data;

public class ThetaDeskDbContext(DbContextOptions<ThetaDeskDbContext> options) : DbContext(options)
{
    public DbSet<Fund> Funds => Set<Fund>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<OptionLeg> OptionLegs => Set<OptionLeg>();
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<GreeksSnapshot> GreeksSnapshots => Set<GreeksSnapshot>();
    public DbSet<MarginSnapshot> MarginSnapshots => Set<MarginSnapshot>();
    public DbSet<RiskLimit> RiskLimits => Set<RiskLimit>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<NavSnapshot> NavSnapshots => Set<NavSnapshot>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<TradeProposal> Proposals => Set<TradeProposal>();
    public DbSet<ProposalLeg> ProposalLegs => Set<ProposalLeg>();
    public DbSet<Adjustment> Adjustments => Set<Adjustment>();
    public DbSet<VolatilitySnapshot> VolatilitySnapshots => Set<VolatilitySnapshot>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<StrategyConfig> StrategyConfigs => Set<StrategyConfig>();
    public DbSet<StrategyLeg> StrategyLegs => Set<StrategyLeg>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Fund>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.StartingCapital).HasPrecision(18, 2);
            e.Property(x => x.CashBalance).HasPrecision(18, 2);
            e.Property(x => x.CurrentNav).HasPrecision(18, 2);
        });

        b.Entity<Position>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Strategy).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.NetCredit).HasPrecision(18, 2);
            e.Property(x => x.MaxLoss).HasPrecision(18, 2);
            e.Property(x => x.MarginBlocked).HasPrecision(18, 2);
            e.Property(x => x.RealisedPnl).HasPrecision(18, 2);
            e.Property(x => x.UnrealisedPnl).HasPrecision(18, 2);
            e.Property(x => x.StopLossPremiumMult).HasPrecision(8, 4);
            e.Property(x => x.ProfitTakePct).HasPrecision(8, 4);
            e.Property(x => x.AdjustTriggerDelta).HasPrecision(8, 4);
            e.HasOne(x => x.Fund).WithMany(f => f.Positions).HasForeignKey(x => x.FundId);
            e.HasOne(x => x.MarginSnapshot).WithOne(m => m.Position).HasForeignKey<MarginSnapshot>(m => m.PositionId);
        });

        b.Entity<StrategyConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Strategy).HasConversion<string>();
            e.Property(x => x.VixMin).HasPrecision(8, 2);
            e.Property(x => x.VixMax).HasPrecision(8, 2);
            e.Property(x => x.SizingPct).HasPrecision(8, 4);
            e.Property(x => x.GttPremiumPct).HasPrecision(8, 4);
            e.Property(x => x.ProfitTargetPct).HasPrecision(8, 4);
            e.Property(x => x.AdjustTriggerDelta).HasPrecision(8, 4);
            e.HasOne(x => x.Fund).WithMany(f => f.Strategies).HasForeignKey(x => x.FundId);
        });

        b.Entity<StrategyLeg>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OptionType).HasConversion<string>();
            e.Property(x => x.Side).HasConversion<string>();
            e.Property(x => x.Expiry).HasConversion<string>();
            e.Property(x => x.TargetDelta).HasPrecision(6, 4);
            e.HasOne(x => x.StrategyConfig).WithMany(c => c.Legs).HasForeignKey(x => x.StrategyConfigId);
        });

        b.Entity<OptionLeg>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OptionType).HasConversion<string>();
            e.Property(x => x.Side).HasConversion<string>();
            e.Property(x => x.EntryPrice).HasPrecision(18, 4);
            e.Property(x => x.CurrentPrice).HasPrecision(18, 4);
            e.HasOne(x => x.Position).WithMany(p => p.Legs).HasForeignKey(x => x.PositionId);
            e.HasOne(x => x.Instrument).WithMany(i => i.Legs).HasForeignKey(x => x.InstrumentToken);
        });

        b.Entity<Instrument>(e =>
        {
            e.HasKey(x => x.InstrumentToken);
            e.Property(x => x.OptionType).HasConversion<string>();
        });

        b.Entity<GreeksSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PositionId, x.TakenAtUtc });
            e.HasOne(x => x.Position).WithMany(p => p.GreeksSnapshots).HasForeignKey(x => x.PositionId);
        });

        b.Entity<MarginSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
        });

        b.Entity<RiskLimit>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Scope).HasConversion<string>();
            e.HasOne(x => x.Fund).WithMany(f => f.RiskLimits).HasForeignKey(x => x.FundId);
        });

        b.Entity<Order>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>();
            e.HasOne(x => x.OptionLeg).WithMany(l => l.Orders).HasForeignKey(x => x.OptionLegId);
        });

        b.Entity<NavSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.FundId, x.AsOf }).IsUnique();
            e.HasOne(x => x.Fund).WithMany(f => f.NavSnapshots).HasForeignKey(x => x.FundId);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.FundId, x.AtUtc });
            e.HasOne(x => x.Fund).WithMany(f => f.AuditLog).HasForeignKey(x => x.FundId);
        });

        b.Entity<TradeProposal>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Strategy).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.GttPremiumPct).HasPrecision(8, 4);
            e.Property(x => x.ProfitTargetPct).HasPrecision(8, 4);
            e.Property(x => x.AdjustTriggerDelta).HasPrecision(8, 4);
            e.HasOne(x => x.Fund).WithMany(f => f.Proposals).HasForeignKey(x => x.FundId);
            e.HasOne(x => x.Position).WithOne(p => p.Proposal)
                .HasForeignKey<TradeProposal>(x => x.PositionId)
                .IsRequired(false);
        });

        b.Entity<ProposalLeg>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OptionType).HasConversion<string>();
            e.Property(x => x.Side).HasConversion<string>();
            e.HasOne(x => x.Proposal).WithMany(p => p.Legs).HasForeignKey(x => x.ProposalId);
        });

        b.Entity<Adjustment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasOne(x => x.Position).WithMany(p => p.Adjustments).HasForeignKey(x => x.PositionId);
        });

        b.Entity<VolatilitySnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.FundId, x.TakenAtUtc });
            e.HasOne(x => x.Fund).WithMany(f => f.VolatilitySnapshots).HasForeignKey(x => x.FundId);
        });

        b.Entity<Alert>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Severity).HasConversion<string>();
            e.HasOne(x => x.Fund).WithMany(f => f.Alerts).HasForeignKey(x => x.FundId);
        });
    }
}
