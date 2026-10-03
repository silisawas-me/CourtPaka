using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class VenueDayStartsHour : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DayStartsHour",
                table: "Venues",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Venues_DayStartsInTheSmallHours",
                table: "Venues",
                sql: "\"DayStartsHour\" BETWEEN 0 AND 6");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Venues_DayStartsInTheSmallHours",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "DayStartsHour",
                table: "Venues");
        }
    }
}
