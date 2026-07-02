using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThetaDesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class VegaFlowDailySnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VegaFlowDailySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TradingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PointsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VegaFlowDailySnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VegaFlowDailySnapshots_TradingDate",
                table: "VegaFlowDailySnapshots",
                column: "TradingDate",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VegaFlowDailySnapshots");
        }
    }
}
