using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenPrintDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Printers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UncPath = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Printers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Zones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultPrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Zones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Zones_Printers_DefaultPrinterId",
                        column: x => x.DefaultPrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ZonePrinters",
                columns: table => new
                {
                    PrintersId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ZonesId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZonePrinters", x => new { x.PrintersId, x.ZonesId });
                    table.ForeignKey(
                        name: "FK_ZonePrinters_Printers_PrintersId",
                        column: x => x.PrintersId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ZonePrinters_Zones_ZonesId",
                        column: x => x.ZonesId,
                        principalTable: "Zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ZoneRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ZoneId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupSid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SubnetCidr = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OuDn = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZoneRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ZoneRules_Zones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "Zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Printers_UncPath",
                table: "Printers",
                column: "UncPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ZonePrinters_ZonesId",
                table: "ZonePrinters",
                column: "ZonesId");

            migrationBuilder.CreateIndex(
                name: "IX_ZoneRules_ZoneId",
                table: "ZoneRules",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_Zones_DefaultPrinterId",
                table: "Zones",
                column: "DefaultPrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_Zones_Name",
                table: "Zones",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ZonePrinters");

            migrationBuilder.DropTable(
                name: "ZoneRules");

            migrationBuilder.DropTable(
                name: "Zones");

            migrationBuilder.DropTable(
                name: "Printers");
        }
    }
}
