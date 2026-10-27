using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FleetApi.Devices.Migrations;

#pragma warning disable CA1861 // Migration arguments are created once.
    /// <inheritdoc />
    public partial class CommandRetries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcknowledgedAtUtc",
                table: "CommandTargets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAtUtc",
                table: "CommandTargets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSentAtUtc",
                table: "CommandTargets",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "ResultDetail",
                table: "CommandTargets",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "CommandTargets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "CommandTargets",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.Sql("UPDATE CommandTargets SET LastSentAtUtc = CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CommandTargets_Status_LastSentAtUtc",
                table: "CommandTargets",
                columns: new[] { "Status", "LastSentAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CommandTargets_Status_LastSentAtUtc",
                table: "CommandTargets");

            migrationBuilder.DropColumn(
                name: "AcknowledgedAtUtc",
                table: "CommandTargets");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "CommandTargets");

            migrationBuilder.DropColumn(
                name: "LastSentAtUtc",
                table: "CommandTargets");

            migrationBuilder.DropColumn(
                name: "ResultDetail",
                table: "CommandTargets");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "CommandTargets");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "CommandTargets");
        }
    }
