using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenPrintDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePrinterLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Location",
                table: "Printers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "Printers",
                type: "TEXT",
                maxLength: 128,
                nullable: true);
        }
    }
}
