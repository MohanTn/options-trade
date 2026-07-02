using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThetaDesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WeeklyCompounding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WeeklyCompounding",
                table: "StrategyConfigs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WeeklyCompounding",
                table: "StrategyConfigs");
        }
    }
}
