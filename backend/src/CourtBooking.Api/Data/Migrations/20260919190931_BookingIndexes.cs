using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BookingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A booker holds one booking at a time (PRD S-22). The handler checks first so the
            // booker gets a reason rather than a failed write, but two of their own requests can
            // pass that check at the same instant — so, like the overlap rule, the database is what
            // actually decides. Status 1 is Held; only a held booking takes part.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_Bookings_OneHeldPerBooker"
                ON "Bookings" ("BookerUserId")
                WHERE "Status" = 1;
                """);

            // Every write releases the holds whose time is up before it reads anything, so this
            // predicate is on the hot path. Without an index leading on Status it is a scan of
            // every booking the platform has ever taken.
            migrationBuilder.Sql("""
                CREATE INDEX "IX_Bookings_Status_HoldExpiresAt"
                ON "Bookings" ("Status", "HoldExpiresAt");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Bookings_Status_HoldExpiresAt";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Bookings_OneHeldPerBooker";""");
        }
    }
}
