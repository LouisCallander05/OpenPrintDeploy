using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenPrintDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSubnetOuAndDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Zones_Printers_DefaultPrinterId",
                table: "Zones");

            migrationBuilder.DropIndex(
                name: "IX_Zones_DefaultPrinterId",
                table: "Zones");

            migrationBuilder.DropColumn(
                name: "DefaultPrinterId",
                table: "Zones");

            migrationBuilder.DropColumn(
                name: "OuDn",
                table: "ZoneRules");

            migrationBuilder.DropColumn(
                name: "SubnetCidr",
                table: "ZoneRules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultPrinterId",
                table: "Zones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OuDn",
                table: "ZoneRules",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubnetCidr",
                table: "ZoneRules",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Zones_DefaultPrinterId",
                table: "Zones",
                column: "DefaultPrinterId");

            migrationBuilder.AddForeignKey(
                name: "FK_Zones_Printers_DefaultPrinterId",
                table: "Zones",
                column: "DefaultPrinterId",
                principalTable: "Printers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
