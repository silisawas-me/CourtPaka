using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TheMonthThePlatformBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommissionInvoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EvidenceKey = table.Column<string>(type: "text", nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PaidByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RefusedReason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommissionInvoices_AspNetUsers_PaidByUserId",
                        column: x => x.PaidByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommissionInvoices_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommissionInvoiceLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    KeptBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    AmountBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionInvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommissionInvoiceLines_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommissionInvoiceLines_CommissionInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "CommissionInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommissionInvoiceLines_BookingId",
                table: "CommissionInvoiceLines",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommissionInvoiceLines_InvoiceId",
                table: "CommissionInvoiceLines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionInvoices_Number",
                table: "CommissionInvoices",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommissionInvoices_PaidByUserId",
                table: "CommissionInvoices",
                column: "PaidByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionInvoices_VenueId_Month",
                table: "CommissionInvoices",
                columns: new[] { "VenueId", "Month" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommissionInvoiceLines");

            migrationBuilder.DropTable(
                name: "CommissionInvoices");
        }
    }
}
