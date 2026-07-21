using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoobackVash.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CharacterIgnoredAndGuild : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GuildName",
                table: "Characters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GuildRank",
                table: "Characters",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GuildSyncedAt",
                table: "Characters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Ignored",
                table: "Characters",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Characters_Ignored",
                table: "Characters",
                column: "Ignored");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Characters_Ignored",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "GuildName",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "GuildRank",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "GuildSyncedAt",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "Ignored",
                table: "Characters");
        }
    }
}
