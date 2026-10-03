using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CounterMoney : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyClosings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    OpeningFloatBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    ExpectedCashBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    CountedCashBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    DifferenceBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyClosings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailyClosings_AspNetUsers_ClosedByUserId",
                        column: x => x.ClosedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DailyClosings_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentReceipts", x => x.Id);
                    table.CheckConstraint("CK_PaymentReceipts_AmountIsMoney", "\"AmountBaht\" > 0");
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_AspNetUsers_ReceivedByUserId",
                        column: x => x.ReceivedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyClosings_ClosedByUserId",
                table: "DailyClosings",
                column: "ClosedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyClosings_VenueId_Date",
                table: "DailyClosings",
                columns: new[] { "VenueId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_BookingId",
                table: "PaymentReceipts",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_ReceivedByUserId",
                table: "PaymentReceipts",
                column: "ReceivedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_VenueId_ReceivedAt",
                table: "PaymentReceipts",
                columns: new[] { "VenueId", "ReceivedAt" });

            // Money that came in and a till that was counted are records, not notes: the database
            // refuses to change or remove them, like every other history table (CLAUDE.md).
            migrationBuilder.Sql("""
                CREATE TRIGGER "PaymentReceipts_permanent"
                    BEFORE UPDATE OR DELETE ON "PaymentReceipts"
                    FOR EACH ROW EXECUTE FUNCTION audit_rows_are_permanent();

                CREATE TRIGGER "PaymentReceipts_not_truncated"
                    BEFORE TRUNCATE ON "PaymentReceipts"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_rows_are_permanent();

                CREATE TRIGGER "DailyClosings_permanent"
                    BEFORE UPDATE OR DELETE ON "DailyClosings"
                    FOR EACH ROW EXECUTE FUNCTION audit_rows_are_permanent();

                CREATE TRIGGER "DailyClosings_not_truncated"
                    BEFORE TRUNCATE ON "DailyClosings"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_rows_are_permanent();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "PaymentReceipts_permanent" ON "PaymentReceipts";
                DROP TRIGGER IF EXISTS "PaymentReceipts_not_truncated" ON "PaymentReceipts";
                DROP TRIGGER IF EXISTS "DailyClosings_permanent" ON "DailyClosings";
                DROP TRIGGER IF EXISTS "DailyClosings_not_truncated" ON "DailyClosings";
                """);

            migrationBuilder.DropTable(
                name: "DailyClosings");

            migrationBuilder.DropTable(
                name: "PaymentReceipts");
        }
    }
}
