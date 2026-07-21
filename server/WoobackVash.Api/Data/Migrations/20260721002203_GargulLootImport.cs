using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoobackVash.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GargulLootImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LootAwards_Characters_CharacterId",
                table: "LootAwards");

            migrationBuilder.DropForeignKey(
                name: "FK_LootAwards_RaidEvents_RaidEventId",
                table: "LootAwards");

            migrationBuilder.AlterColumn<Guid>(
                name: "RaidEventId",
                table: "LootAwards",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "CharacterId",
                table: "LootAwards",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "AwardedBy",
                table: "LootAwards",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Checksum",
                table: "LootAwards",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Disenchanted",
                table: "LootAwards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OffSpec",
                table: "LootAwards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PlusOne",
                table: "LootAwards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SoftReserve",
                table: "LootAwards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Tmb",
                table: "LootAwards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "WinnerClass",
                table: "LootAwards",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Wishlist",
                table: "LootAwards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "LootRolls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LootAwardId = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    Classification = table.Column<string>(type: "text", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: true),
                    PlusOneState = table.Column<int>(type: "integer", nullable: true),
                    Class = table.Column<string>(type: "text", nullable: true),
                    RolledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LootRolls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LootRolls_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LootRolls_LootAwards_LootAwardId",
                        column: x => x.LootAwardId,
                        principalTable: "LootAwards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LootAwards_Checksum",
                table: "LootAwards",
                column: "Checksum",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LootRolls_CharacterId",
                table: "LootRolls",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_LootRolls_LootAwardId",
                table: "LootRolls",
                column: "LootAwardId");

            migrationBuilder.AddForeignKey(
                name: "FK_LootAwards_Characters_CharacterId",
                table: "LootAwards",
                column: "CharacterId",
                principalTable: "Characters",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_LootAwards_RaidEvents_RaidEventId",
                table: "LootAwards",
                column: "RaidEventId",
                principalTable: "RaidEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LootAwards_Characters_CharacterId",
                table: "LootAwards");

            migrationBuilder.DropForeignKey(
                name: "FK_LootAwards_RaidEvents_RaidEventId",
                table: "LootAwards");

            migrationBuilder.DropTable(
                name: "LootRolls");

            migrationBuilder.DropIndex(
                name: "IX_LootAwards_Checksum",
                table: "LootAwards");

            migrationBuilder.DropColumn(
                name: "AwardedBy",
                table: "LootAwards");

            migrationBuilder.DropColumn(
                name: "Checksum",
                table: "LootAwards");

            migrationBuilder.DropColumn(
                name: "Disenchanted",
                table: "LootAwards");

            migrationBuilder.DropColumn(
                name: "OffSpec",
                table: "LootAwards");

            migrationBuilder.DropColumn(
                name: "PlusOne",
                table: "LootAwards");

            migrationBuilder.DropColumn(
                name: "SoftReserve",
                table: "LootAwards");

            migrationBuilder.DropColumn(
                name: "Tmb",
                table: "LootAwards");

            migrationBuilder.DropColumn(
                name: "WinnerClass",
                table: "LootAwards");

            migrationBuilder.DropColumn(
                name: "Wishlist",
                table: "LootAwards");

            migrationBuilder.AlterColumn<Guid>(
                name: "RaidEventId",
                table: "LootAwards",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "CharacterId",
                table: "LootAwards",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LootAwards_Characters_CharacterId",
                table: "LootAwards",
                column: "CharacterId",
                principalTable: "Characters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LootAwards_RaidEvents_RaidEventId",
                table: "LootAwards",
                column: "RaidEventId",
                principalTable: "RaidEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
