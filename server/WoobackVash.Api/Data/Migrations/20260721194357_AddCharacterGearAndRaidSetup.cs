using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoobackVash.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterGearAndRaidSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Characters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SetupUpdatedAt",
                table: "Characters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GearSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    RaidEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    WclReportCode = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Spec = table.Column<string>(type: "text", nullable: true),
                    ItemLevel = table.Column<double>(type: "double precision", nullable: true),
                    Items = table.Column<string>(type: "jsonb", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GearSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GearSnapshots_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GearSnapshots_RaidEvents_RaidEventId",
                        column: x => x.RaidEventId,
                        principalTable: "RaidEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GearSnapshots_CharacterId_WclReportCode",
                table: "GearSnapshots",
                columns: new[] { "CharacterId", "WclReportCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GearSnapshots_RaidEventId",
                table: "GearSnapshots",
                column: "RaidEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GearSnapshots");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "SetupUpdatedAt",
                table: "Characters");
        }
    }
}
