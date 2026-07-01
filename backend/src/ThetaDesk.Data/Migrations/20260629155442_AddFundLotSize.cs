using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThetaDesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFundLotSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LotSize",
                table: "Funds",
                type: "integer",
                nullable: false,
                defaultValue: 65); // NIFTY lot size; backfills the existing fund row.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LotSize",
                table: "Funds");
        }
    }
}
