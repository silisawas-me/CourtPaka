using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BookingRefundPercent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RefundPercent",
                table: "Bookings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // The bookings US-12 already turned away carry the whole amount, and their
            // RefundDueBaht says so. Without this they would read as owing nothing the next time
            // the amount is worked out again (PRD 6.2). 7 is BookingStatus.Rejected.
            migrationBuilder.Sql("""UPDATE "Bookings" SET "RefundPercent" = 100 WHERE "Status" = 7;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RefundPercent",
                table: "Bookings");
        }
    }
}
