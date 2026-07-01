using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ThetaDesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialThetaDesk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Funds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    StartingCapital = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CashBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrentNav = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MonthlyTargetPct = table.Column<decimal>(type: "numeric", nullable: false),
                    MaxMarginUtilPct = table.Column<decimal>(type: "numeric", nullable: false),
                    PerPositionMaxLoss = table.Column<decimal>(type: "numeric", nullable: false),
                    DrawdownStopPct = table.Column<decimal>(type: "numeric", nullable: false),
                    ProfitTakePct = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Funds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Instruments",
                columns: table => new
                {
                    InstrumentToken = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradingSymbol = table.Column<string>(type: "text", nullable: false),
                    Strike = table.Column<decimal>(type: "numeric", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OptionType = table.Column<string>(type: "text", nullable: false),
                    LotSize = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instruments", x => x.InstrumentToken);
                });

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FundId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    RaisedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Acknowledged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alerts_Funds_FundId",
                        column: x => x.FundId,
                        principalTable: "Funds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FundId = table.Column<Guid>(type: "uuid", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    BeforeJson = table.Column<string>(type: "text", nullable: true),
                    AfterJson = table.Column<string>(type: "text", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HashPrev = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditLog_Funds_FundId",
                        column: x => x.FundId,
                        principalTable: "Funds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NavSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FundId = table.Column<Guid>(type: "uuid", nullable: false),
                    AsOf = table.Column<DateOnly>(type: "date", nullable: false),
                    Nav = table.Column<decimal>(type: "numeric", nullable: false),
                    DailyPnl = table.Column<decimal>(type: "numeric", nullable: false),
                    Charges = table.Column<decimal>(type: "numeric", nullable: false),
                    MonthToDatePct = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NavSnapshots_Funds_FundId",
                        column: x => x.FundId,
                        principalTable: "Funds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FundId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Strategy = table.Column<string>(type: "text", nullable: false),
                    IsDefinedRisk = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EntryDte = table.Column<int>(type: "integer", nullable: false),
                    TargetExitDte = table.Column<int>(type: "integer", nullable: false),
                    NetCredit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxLoss = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MarginBlocked = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RealisedPnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UnrealisedPnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    GttStopOrderId = table.Column<string>(type: "text", nullable: true),
                    StopLossPremiumMult = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Positions_Funds_FundId",
                        column: x => x.FundId,
                        principalTable: "Funds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RiskLimits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FundId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "text", nullable: false),
                    Metric = table.Column<string>(type: "text", nullable: false),
                    LowerBound = table.Column<decimal>(type: "numeric", nullable: true),
                    UpperBound = table.Column<decimal>(type: "numeric", nullable: true),
                    Hard = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskLimits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskLimits_Funds_FundId",
                        column: x => x.FundId,
                        principalTable: "Funds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolatilitySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FundId = table.Column<Guid>(type: "uuid", nullable: false),
                    TakenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IndiaVix = table.Column<decimal>(type: "numeric", nullable: false),
                    AtmIv = table.Column<decimal>(type: "numeric", nullable: false),
                    IvRank = table.Column<decimal>(type: "numeric", nullable: false),
                    IvPercentile = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolatilitySnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolatilitySnapshots_Funds_FundId",
                        column: x => x.FundId,
                        principalTable: "Funds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Adjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    TriggerReason = table.Column<string>(type: "text", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GreeksBeforeJson = table.Column<string>(type: "text", nullable: true),
                    GreeksAfterJson = table.Column<string>(type: "text", nullable: true),
                    Automated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Adjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Adjustments_Positions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "Positions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GreeksSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TakenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Delta = table.Column<decimal>(type: "numeric", nullable: false),
                    Gamma = table.Column<decimal>(type: "numeric", nullable: false),
                    Theta = table.Column<decimal>(type: "numeric", nullable: false),
                    Vega = table.Column<decimal>(type: "numeric", nullable: false),
                    Rho = table.Column<decimal>(type: "numeric", nullable: false),
                    Iv = table.Column<decimal>(type: "numeric", nullable: false),
                    UnderlyingSpot = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GreeksSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GreeksSnapshots_Positions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "Positions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MarginSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Span = table.Column<decimal>(type: "numeric", nullable: false),
                    Exposure = table.Column<decimal>(type: "numeric", nullable: false),
                    Total = table.Column<decimal>(type: "numeric", nullable: false),
                    UtilisationPct = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarginSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarginSnapshots_Positions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "Positions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OptionLegs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstrumentToken = table.Column<long>(type: "bigint", nullable: false),
                    OptionType = table.Column<string>(type: "text", nullable: false),
                    Side = table.Column<string>(type: "text", nullable: false),
                    Strike = table.Column<decimal>(type: "numeric", nullable: false),
                    Lots = table.Column<int>(type: "integer", nullable: false),
                    Qty = table.Column<int>(type: "integer", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionLegs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OptionLegs_Instruments_InstrumentToken",
                        column: x => x.InstrumentToken,
                        principalTable: "Instruments",
                        principalColumn: "InstrumentToken",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OptionLegs_Positions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "Positions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Proposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FundId = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: true),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Strategy = table.Column<string>(type: "text", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IndiaVix = table.Column<decimal>(type: "numeric", nullable: false),
                    AtmIv = table.Column<decimal>(type: "numeric", nullable: false),
                    IvRank = table.Column<decimal>(type: "numeric", nullable: false),
                    Score = table.Column<decimal>(type: "numeric", nullable: false),
                    ExpectedReturnPct = table.Column<decimal>(type: "numeric", nullable: false),
                    Rationale = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    EntryDte = table.Column<int>(type: "integer", nullable: false),
                    TargetExitDte = table.Column<int>(type: "integer", nullable: false),
                    Lots = table.Column<int>(type: "integer", nullable: false),
                    Qty = table.Column<int>(type: "integer", nullable: false),
                    NetCredit = table.Column<decimal>(type: "numeric", nullable: false),
                    MaxLoss = table.Column<decimal>(type: "numeric", nullable: false),
                    MarginBlocked = table.Column<decimal>(type: "numeric", nullable: false),
                    MarginUtilPct = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Proposals_Funds_FundId",
                        column: x => x.FundId,
                        principalTable: "Funds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Proposals_Positions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "Positions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OptionLegId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrokerOrderId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    FillPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    Slippage = table.Column<decimal>(type: "numeric", nullable: false),
                    PlacedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Orders_OptionLegs_OptionLegId",
                        column: x => x.OptionLegId,
                        principalTable: "OptionLegs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProposalLegs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uuid", nullable: false),
                    OptionType = table.Column<string>(type: "text", nullable: false),
                    Side = table.Column<string>(type: "text", nullable: false),
                    Strike = table.Column<decimal>(type: "numeric", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TradingSymbol = table.Column<string>(type: "text", nullable: false),
                    InstrumentToken = table.Column<long>(type: "bigint", nullable: false),
                    MidPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    Delta = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProposalLegs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProposalLegs_Proposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "Proposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Adjustments_PositionId",
                table: "Adjustments",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_FundId",
                table: "Alerts",
                column: "FundId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_FundId_AtUtc",
                table: "AuditLog",
                columns: new[] { "FundId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GreeksSnapshots_PositionId_TakenAtUtc",
                table: "GreeksSnapshots",
                columns: new[] { "PositionId", "TakenAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarginSnapshots_PositionId",
                table: "MarginSnapshots",
                column: "PositionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NavSnapshots_FundId_AsOf",
                table: "NavSnapshots",
                columns: new[] { "FundId", "AsOf" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptionLegs_InstrumentToken",
                table: "OptionLegs",
                column: "InstrumentToken");

            migrationBuilder.CreateIndex(
                name: "IX_OptionLegs_PositionId",
                table: "OptionLegs",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_OptionLegId",
                table: "Orders",
                column: "OptionLegId");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_FundId",
                table: "Positions",
                column: "FundId");

            migrationBuilder.CreateIndex(
                name: "IX_ProposalLegs_ProposalId",
                table: "ProposalLegs",
                column: "ProposalId");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_FundId",
                table: "Proposals",
                column: "FundId");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_PositionId",
                table: "Proposals",
                column: "PositionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskLimits_FundId",
                table: "RiskLimits",
                column: "FundId");

            migrationBuilder.CreateIndex(
                name: "IX_VolatilitySnapshots_FundId_TakenAtUtc",
                table: "VolatilitySnapshots",
                columns: new[] { "FundId", "TakenAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Adjustments");

            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "GreeksSnapshots");

            migrationBuilder.DropTable(
                name: "MarginSnapshots");

            migrationBuilder.DropTable(
                name: "NavSnapshots");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "ProposalLegs");

            migrationBuilder.DropTable(
                name: "RiskLimits");

            migrationBuilder.DropTable(
                name: "VolatilitySnapshots");

            migrationBuilder.DropTable(
                name: "OptionLegs");

            migrationBuilder.DropTable(
                name: "Proposals");

            migrationBuilder.DropTable(
                name: "Instruments");

            migrationBuilder.DropTable(
                name: "Positions");

            migrationBuilder.DropTable(
                name: "Funds");
        }
    }
}
