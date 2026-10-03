using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TheOfferBehindTheQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OfferedBookingId",
                table: "WaitlistEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_OfferedBookingId",
                table: "WaitlistEntries",
                column: "OfferedBookingId");

            migrationBuilder.AddForeignKey(
                name: "FK_WaitlistEntries_Bookings_OfferedBookingId",
                table: "WaitlistEntries",
                column: "OfferedBookingId",
                principalTable: "Bookings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WaitlistEntries_Bookings_OfferedBookingId",
                table: "WaitlistEntries");

            migrationBuilder.DropIndex(
                name: "IX_WaitlistEntries_OfferedBookingId",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "OfferedBookingId",
                table: "WaitlistEntries");
        }
    }
}
