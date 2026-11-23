using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenPrintDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClientRepairRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RepairCompletedAt",
                table: "ClientUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepairError",
                table: "ClientUsers",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RepairRequestId",
                table: "ClientUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RepairRequestedAt",
                table: "ClientUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepairRequestedBy",
                table: "ClientUsers",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RepairStartedAt",
                table: "ClientUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepairStatus",
                table: "ClientUsers",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsPrinterRepair",
                table: "ClientUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepairCompletedAt",
                table: "ClientUsers");

            migrationBuilder.DropColumn(
                name: "RepairError",
                table: "ClientUsers");

            migrationBuilder.DropColumn(
                name: "RepairRequestId",
                table: "ClientUsers");

            migrationBuilder.DropColumn(
                name: "RepairRequestedAt",
                table: "ClientUsers");

            migrationBuilder.DropColumn(
                name: "RepairRequestedBy",
                table: "ClientUsers");

            migrationBuilder.DropColumn(
                name: "RepairStartedAt",
                table: "ClientUsers");

            migrationBuilder.DropColumn(
                name: "RepairStatus",
                table: "ClientUsers");

            migrationBuilder.DropColumn(
                name: "SupportsPrinterRepair",
                table: "ClientUsers");
        }
    }
}
