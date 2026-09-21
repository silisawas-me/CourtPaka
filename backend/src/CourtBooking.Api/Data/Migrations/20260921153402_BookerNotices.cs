using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BookerNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookerNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookerNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookerNotices_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefundRecords_RecordedAt",
                table: "RefundRecords",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BookingStatusChanges_ChangedAt",
                table: "BookingStatusChanges",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSlots_StartsAt",
                table: "BookingSlots",
                column: "StartsAt",
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_BookerNotices_BookingId",
                table: "BookerNotices",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_BookerNotices_SourceId_Kind",
                table: "BookerNotices",
                columns: new[] { "SourceId", "Kind" },
                unique: true);

            // Everything that happened before bookers were told about anything counts as told.
            // Without this, the first sweep after deploying would mail every booker about the
            // last day of their history at once (PRD US-06). A row whose From and To match is a
            // venue settling a payment (BookingTransitions.Settled), told as PaymentSettled.
            migrationBuilder.Sql("""
                INSERT INTO "BookerNotices" ("Id", "BookingId", "Kind", "SourceId", "ClaimedAt")
                SELECT gen_random_uuid(), c."BookingId",
                       CASE WHEN c."From" = c."To" THEN 8
                            WHEN c."To" = 1 THEN 1 WHEN c."To" = 3 THEN 2 WHEN c."To" = 7 THEN 3
                            WHEN c."To" = 6 THEN 4 ELSE 5 END,
                       c."Id", now()
                FROM "BookingStatusChanges" c
                WHERE c."To" IN (1, 3, 5, 6, 7);

                INSERT INTO "BookerNotices" ("Id", "BookingId", "Kind", "SourceId", "ClaimedAt")
                SELECT gen_random_uuid(), r."BookingId", 6, r."Id", now()
                FROM "RefundRecords" r;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookerNotices");

            migrationBuilder.DropIndex(
                name: "IX_RefundRecords_RecordedAt",
                table: "RefundRecords");

            migrationBuilder.DropIndex(
                name: "IX_BookingStatusChanges_ChangedAt",
                table: "BookingStatusChanges");

            migrationBuilder.DropIndex(
                name: "IX_BookingSlots_StartsAt",
                table: "BookingSlots");
        }
    }
}
