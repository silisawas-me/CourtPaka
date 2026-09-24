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

            // And the rule itself is held at the database, the way the share beside it is: a row
            // written by anything but the endpoint — a data fix, a partial restore — with a
            // threshold of nought would floor every booker at half and tell every one of them
            // they have a history here (PRD US-28).
            migrationBuilder.Sql(
                @"ALTER TABLE ""Venues"" ADD CONSTRAINT ""CK_Venues_RiskIsCountable"" "
                + @"CHECK (""Risk_LookbackDays"" BETWEEN 1 AND 365 "
                + @"AND ""Risk_HalfAt"" BETWEEN 1 AND 50 "
                + @"AND ""Risk_FullAt"" BETWEEN ""Risk_HalfAt"" AND 50);");

            migrationBuilder.Sql(
                @"ALTER TABLE ""Venues"" ADD CONSTRAINT ""CK_Venues_PeakIsAWindow"" "
                + @"CHECK ((""Risk_PeakFromHour"" IS NULL AND ""Risk_PeakUntilHour"" IS NULL) "
                + @"OR (""Risk_PeakFromHour"" BETWEEN 0 AND 23 "
                + @"AND ""Risk_PeakUntilHour"" BETWEEN 1 AND 24 "
                + @"AND ""Risk_PeakFromHour"" < ""Risk_PeakUntilHour""));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE ""Venues"" DROP CONSTRAINT IF EXISTS ""CK_Venues_PeakIsAWindow"";");
            migrationBuilder.Sql(
                @"ALTER TABLE ""Venues"" DROP CONSTRAINT IF EXISTS ""CK_Venues_RiskIsCountable"";");

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
