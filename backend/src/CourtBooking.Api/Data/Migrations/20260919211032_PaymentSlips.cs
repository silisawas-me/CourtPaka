using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentSlips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentSlips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StoredName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ByteSize = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SameBytesAsSlipId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentSlips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentSlips_AspNetUsers_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentSlips_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSlips_BookingId_UploadedAt",
                table: "PaymentSlips",
                columns: new[] { "BookingId", "UploadedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSlips_Sha256",
                table: "PaymentSlips",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSlips_UploadedByUserId",
                table: "PaymentSlips",
                column: "UploadedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentSlips");
        }
    }
}
