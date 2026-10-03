using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArrivalAndCheckIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GraceMinutes",
                table: "Venues",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<int>(
                name: "Arrival",
                table: "Bookings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArrivedAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookingArrivalChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<int>(type: "integer", nullable: false),
                    To = table.Column<int>(type: "integer", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingArrivalChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingArrivalChanges_AspNetUsers_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingArrivalChanges_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingArrivalChanges_BookingId_ChangedAt",
                table: "BookingArrivalChanges",
                columns: new[] { "BookingId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingArrivalChanges_ChangedByUserId",
                table: "BookingArrivalChanges",
                column: "ChangedByUserId");

            // A history table, so the database refuses to rewrite it — the same pair of triggers
            // every other one carries (migration AuditRowsArePermanent, CLAUDE.md).
            migrationBuilder.Sql("""
                CREATE TRIGGER "BookingArrivalChanges_permanent"
                    BEFORE UPDATE OR DELETE ON "BookingArrivalChanges"
                    FOR EACH ROW EXECUTE FUNCTION audit_rows_are_permanent();

                CREATE TRIGGER "BookingArrivalChanges_not_truncated"
                    BEFORE TRUNCATE ON "BookingArrivalChanges"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_rows_are_permanent();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "BookingArrivalChanges_permanent" ON "BookingArrivalChanges";
                DROP TRIGGER IF EXISTS "BookingArrivalChanges_not_truncated" ON "BookingArrivalChanges";
                """);

            migrationBuilder.DropTable(
                name: "BookingArrivalChanges");

            migrationBuilder.DropColumn(
                name: "GraceMinutes",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Arrival",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ArrivedAt",
                table: "Bookings");
        }
    }
}
