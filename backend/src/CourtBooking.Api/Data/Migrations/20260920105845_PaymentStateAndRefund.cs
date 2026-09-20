using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentStateAndRefund : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NotReceived, which is where a booking starts. The generated default was 0, which
            // is not a PaymentState at all: every booking that existed before this column would
            // have read back as "0" (PRD 6.2).
            migrationBuilder.AddColumn<int>(
                name: "PaymentState",
                table: "Bookings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<decimal>(
                name: "RefundDueBaht",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaymentState",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "RefundDueBaht",
                table: "Bookings");
        }
    }
}
