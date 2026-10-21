using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenPrintDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClientActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    NormalizedMachineName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ClientVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSyncId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastSyncStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSyncCompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AssignedPrinterCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SyncedPrinterCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedPrinterCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientUsers_ClientDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "ClientDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientActivities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SyncId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PrinterDisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PrinterUncPath = table.Column<string>(type: "TEXT", maxLength: 260, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientActivities_ClientUsers_ClientUserId",
                        column: x => x.ClientUserId,
                        principalTable: "ClientUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientPrinters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UncPath = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    NormalizedUncPath = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastOperation = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPrinters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPrinters_ClientUsers_ClientUserId",
                        column: x => x.ClientUserId,
                        principalTable: "ClientUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientActivities_ClientUserId_OccurredAt",
                table: "ClientActivities",
                columns: new[] { "ClientUserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientDevices_NormalizedMachineName",
                table: "ClientDevices",
                column: "NormalizedMachineName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPrinters_ClientUserId_NormalizedUncPath",
                table: "ClientPrinters",
                columns: new[] { "ClientUserId", "NormalizedUncPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUsers_DeviceId_NormalizedUsername",
                table: "ClientUsers",
                columns: new[] { "DeviceId", "NormalizedUsername" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientActivities");

            migrationBuilder.DropTable(
                name: "ClientPrinters");

            migrationBuilder.DropTable(
                name: "ClientUsers");

            migrationBuilder.DropTable(
                name: "ClientDevices");
        }
    }
}
