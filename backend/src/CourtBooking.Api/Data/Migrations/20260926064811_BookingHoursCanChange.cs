using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BookingHoursCanChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookingSlotChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    What = table.Column<int>(type: "integer", nullable: false),
                    FromCourtId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToCourtId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    BahtPerHour = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingSlotChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingSlotChanges_AspNetUsers_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingSlotChanges_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookingSlotChanges_Courts_FromCourtId",
                        column: x => x.FromCourtId,
                        principalTable: "Courts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingSlotChanges_Courts_ToCourtId",
                        column: x => x.ToCourtId,
                        principalTable: "Courts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingSlotChanges_BookingId_ChangedAt",
                table: "BookingSlotChanges",
                columns: new[] { "BookingId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingSlotChanges_ChangedByUserId",
                table: "BookingSlotChanges",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSlotChanges_FromCourtId",
                table: "BookingSlotChanges",
                column: "FromCourtId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSlotChanges_ToCourtId",
                table: "BookingSlotChanges",
                column: "ToCourtId");

            // A history table, so the database refuses to rewrite it — the same pair of triggers
            // every other one carries (migration AuditRowsArePermanent, CLAUDE.md).
            migrationBuilder.Sql("""
                CREATE TRIGGER "BookingSlotChanges_permanent"
                    BEFORE UPDATE OR DELETE ON "BookingSlotChanges"
                    FOR EACH ROW EXECUTE FUNCTION audit_rows_are_permanent();

                CREATE TRIGGER "BookingSlotChanges_not_truncated"
                    BEFORE TRUNCATE ON "BookingSlotChanges"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_rows_are_permanent();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "BookingSlotChanges_permanent" ON "BookingSlotChanges";
                DROP TRIGGER IF EXISTS "BookingSlotChanges_not_truncated" ON "BookingSlotChanges";
                """);

            migrationBuilder.DropTable(
                name: "BookingSlotChanges");
        }
    }
}
