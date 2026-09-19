using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Bookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HoldExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TotalBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    CancellationPolicyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bookings_AspNetUsers_BookerUserId",
                        column: x => x.BookerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Bookings_CancellationPolicies_CancellationPolicyId",
                        column: x => x.CancellationPolicyId,
                        principalTable: "CancellationPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Bookings_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookingSlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourtId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    BahtPerHour = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingSlots_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookingSlots_Courts_CourtId",
                        column: x => x.CourtId,
                        principalTable: "Courts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_BookerUserId_Status",
                table: "Bookings",
                columns: new[] { "BookerUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CancellationPolicyId",
                table: "Bookings",
                column: "CancellationPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_VenueId_CreatedAt",
                table: "Bookings",
                columns: new[] { "VenueId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingSlots_BookingId",
                table: "BookingSlots",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSlots_CourtId_StartsAt",
                table: "BookingSlots",
                columns: new[] { "CourtId", "StartsAt" });

            // Two people may not hold the same court at the same time (PRD BR-04). The rule is the
            // database's: two requests that pass an application check at the same moment would
            // both write, and one of them has to fail. Only rows marked active take part, so
            // cancelling or letting a hold lapse puts the hour back on sale by flipping one column.
            //
            // gist needs btree_gist to index the equality half of the constraint, which is what
            // lets "same court" and "overlapping range" be one index.
            migrationBuilder.Sql("""CREATE EXTENSION IF NOT EXISTS btree_gist;""");
            migrationBuilder.Sql("""
                ALTER TABLE "BookingSlots"
                ADD CONSTRAINT "CK_BookingSlots_NoOverlap"
                EXCLUDE USING gist (
                    "CourtId" WITH =,
                    tstzrange("StartsAt", "EndsAt", '[)') WITH &&
                ) WHERE ("IsActive");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """ALTER TABLE "BookingSlots" DROP CONSTRAINT IF EXISTS "CK_BookingSlots_NoOverlap";""");

            migrationBuilder.DropTable(
                name: "BookingSlots");

            migrationBuilder.DropTable(
                name: "Bookings");
        }
    }
}
