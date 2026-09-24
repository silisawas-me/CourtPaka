using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WhoDoesNotTurnUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Risk_FullAt",
                table: "Venues",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "Risk_HalfAt",
                table: "Venues",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "Risk_LookbackDays",
                table: "Venues",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<bool>(
                name: "Risk_On",
                table: "Venues",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Risk_PeakFromHour",
                table: "Venues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Risk_PeakUntilHour",
                table: "Venues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DepositReason",
                table: "Bookings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // The defaults above exist only to fill the rows that were already there: every venue
            // gets the rule PRD US-28 describes, and every booking made before it says it was
            // asked for what the venue asks everybody. Left in place they would be defaults the
            // model does not know about — and a default of true on a bool is a column that can
            // never be written false.
            foreach (var column in new[]
                     { "Risk_On", "Risk_LookbackDays", "Risk_HalfAt", "Risk_FullAt" })
            {
                migrationBuilder.Sql(
                    $@"ALTER TABLE ""Venues"" ALTER COLUMN ""{column}"" DROP DEFAULT;");
            }

            migrationBuilder.Sql(
                @"ALTER TABLE ""Bookings"" ALTER COLUMN ""DepositReason"" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Risk_FullAt",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Risk_HalfAt",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Risk_LookbackDays",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Risk_On",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Risk_PeakFromHour",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Risk_PeakUntilHour",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "DepositReason",
                table: "Bookings");
        }
    }
}
