using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThetaDesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConfigurableStrategies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AdjustTriggerDelta",
                table: "Proposals",
                type: "numeric(8,4)",
                precision: 8,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "GttEnabled",
                table: "Proposals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "GttPremiumPct",
                table: "Proposals",
                type: "numeric(8,4)",
                precision: 8,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ProfitTargetPct",
                table: "Proposals",
                type: "numeric(8,4)",
                precision: 8,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AlterColumn<decimal>(
                name: "StopLossPremiumMult",
                table: "Positions",
                type: "numeric(8,4)",
                precision: 8,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AddColumn<decimal>(
                name: "AdjustTriggerDelta",
                table: "Positions",
                type: "numeric(8,4)",
                precision: 8,
                scale: 4,
                nullable: false,
                defaultValue: 0.30m); // backfill existing open positions with the prior hardcoded threshold

            migrationBuilder.AddColumn<decimal>(
                name: "ProfitTakePct",
                table: "Positions",
                type: "numeric(8,4)",
                precision: 8,
                scale: 4,
                nullable: false,
                defaultValue: 50m); // backfill existing open positions with the prior hardcoded 50% target

            migrationBuilder.CreateTable(
                name: "StrategyConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FundId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Strategy = table.Column<string>(type: "text", nullable: false),
                    VixMin = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    VixMax = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    EntryDteMin = table.Column<int>(type: "integer", nullable: false),
                    EntryDteMax = table.Column<int>(type: "integer", nullable: false),
                    SizingPct = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    GttEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    GttPremiumPct = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    ProfitTargetPct = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    TargetExitDte = table.Column<int>(type: "integer", nullable: false),
                    AdjustTriggerDelta = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrategyConfigs_Funds_FundId",
                        column: x => x.FundId,
                        principalTable: "Funds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StrategyLegs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StrategyConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    OptionType = table.Column<string>(type: "text", nullable: false),
                    Side = table.Column<string>(type: "text", nullable: false),
                    TargetDelta = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    Expiry = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyLegs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrategyLegs_StrategyConfigs_StrategyConfigId",
                        column: x => x.StrategyConfigId,
                        principalTable: "StrategyConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyConfigs_FundId",
                table: "StrategyConfigs",
                column: "FundId");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyLegs_StrategyConfigId",
                table: "StrategyLegs",
                column: "StrategyConfigId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StrategyLegs");

            migrationBuilder.DropTable(
                name: "StrategyConfigs");

            migrationBuilder.DropColumn(
                name: "AdjustTriggerDelta",
                table: "Proposals");

            migrationBuilder.DropColumn(
                name: "GttEnabled",
                table: "Proposals");

            migrationBuilder.DropColumn(
                name: "GttPremiumPct",
                table: "Proposals");

            migrationBuilder.DropColumn(
                name: "ProfitTargetPct",
                table: "Proposals");

            migrationBuilder.DropColumn(
                name: "AdjustTriggerDelta",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "ProfitTakePct",
                table: "Positions");

            migrationBuilder.AlterColumn<decimal>(
                name: "StopLossPremiumMult",
                table: "Positions",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(8,4)",
                oldPrecision: 8,
                oldScale: 4);
        }
    }
}
