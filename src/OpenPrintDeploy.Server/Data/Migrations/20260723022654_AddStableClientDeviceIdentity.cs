using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenPrintDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStableClientDeviceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientDevices_NormalizedMachineName",
                table: "ClientDevices");

            migrationBuilder.AddColumn<string>(
                name: "DeviceIdentifier",
                table: "ClientDevices",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientDevices_DeviceIdentifier",
                table: "ClientDevices",
                column: "DeviceIdentifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientDevices_NormalizedMachineName",
                table: "ClientDevices",
                column: "NormalizedMachineName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientDevices_DeviceIdentifier",
                table: "ClientDevices");

            migrationBuilder.DropIndex(
                name: "IX_ClientDevices_NormalizedMachineName",
                table: "ClientDevices");

            migrationBuilder.DropColumn(
                name: "DeviceIdentifier",
                table: "ClientDevices");

            migrationBuilder.CreateIndex(
                name: "IX_ClientDevices_NormalizedMachineName",
                table: "ClientDevices",
                column: "NormalizedMachineName",
                unique: true);
        }
    }
}
