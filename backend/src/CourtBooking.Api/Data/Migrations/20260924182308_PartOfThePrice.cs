using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartOfThePrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DepositPercent",
                table: "Venues",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<decimal>(
                name: "DepositBaht",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Every booking made before this column existed was held on the whole of its price,
            // and nothing else would say so: left at nought, a cancellation would give back
            // nothing and the desk would be told to collect a price that had already arrived.
            migrationBuilder.Sql(@"UPDATE ""Bookings"" SET ""DepositBaht"" = ""TotalBaht"";");

            // And the default goes with it. It exists only so that the column could be added to a
            // table that already had rows; left in place it is a default the model does not know
            // about, and a booking written by anything but EF would hold its hours for nought.
            migrationBuilder.Sql(@"ALTER TABLE ""Bookings"" ALTER COLUMN ""DepositBaht"" DROP DEFAULT;");

            // The range is a rule about money, so the database holds it too: a venue asking for
            // nought would hold hours for nothing, and one asking for more than everything would
            // ask a booker for money the booking never cost (PRD US-28).
            migrationBuilder.Sql(
                @"ALTER TABLE ""Venues"" ADD CONSTRAINT ""CK_Venues_DepositIsAShare"" "
                + @"CHECK (""DepositPercent"" BETWEEN 10 AND 100);");

            migrationBuilder.Sql(
                @"ALTER TABLE ""Bookings"" ADD CONSTRAINT ""CK_Bookings_DepositIsPartOfThePrice"" "
                + @"CHECK (""DepositBaht"" > 0 AND ""DepositBaht"" <= ""TotalBaht"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE ""Bookings"" DROP CONSTRAINT IF EXISTS ""CK_Bookings_DepositIsPartOfThePrice"";");
            migrationBuilder.Sql(
                @"ALTER TABLE ""Venues"" DROP CONSTRAINT IF EXISTS ""CK_Venues_DepositIsAShare"";");

            migrationBuilder.DropColumn(
                name: "DepositPercent",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "DepositBaht",
                table: "Bookings");
        }
    }
}
