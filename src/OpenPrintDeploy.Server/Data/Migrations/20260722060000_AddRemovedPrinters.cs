using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenPrintDeploy.Server.Data;

#nullable disable

namespace OpenPrintDeploy.Server.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260722060000_AddRemovedPrinters")]
    public partial class AddRemovedPrinters : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemovedPrinters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UncPath = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_RemovedPrinters", x => x.Id));

            migrationBuilder.CreateIndex(
                name: "IX_RemovedPrinters_UncPath",
                table: "RemovedPrinters",
                column: "UncPath",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable(name: "RemovedPrinters");
    }
}
