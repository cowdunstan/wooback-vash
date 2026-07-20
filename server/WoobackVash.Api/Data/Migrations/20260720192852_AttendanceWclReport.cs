using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoobackVash.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AttendanceWclReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WclReportCode",
                table: "RaidEvents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RaidEvents_WclReportCode",
                table: "RaidEvents",
                column: "WclReportCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RaidEvents_WclReportCode",
                table: "RaidEvents");

            migrationBuilder.DropColumn(
                name: "WclReportCode",
                table: "RaidEvents");
        }
    }
}
