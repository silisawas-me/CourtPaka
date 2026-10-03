using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TheSameHourEveryWeek : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Bookings_BookerOrCustomer",
                table: "Bookings");

            migrationBuilder.AddColumn<Guid>(
                name: "SeriesId",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookingSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourtId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<int>(type: "integer", nullable: false),
                    FromHour = table.Column<int>(type: "integer", nullable: false),
                    UntilHour = table.Column<int>(type: "integer", nullable: false),
                    CustomerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CustomerPhone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    UntilOn = table.Column<DateOnly>(type: "date", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReplacedSeriesId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingSeries", x => x.Id);
                    table.CheckConstraint("CK_BookingSeries_HoursAreAWindow", "\"FromHour\" >= 0 AND \"UntilHour\" <= 24 AND \"FromHour\" < \"UntilHour\"");
                    table.ForeignKey(
                        name: "FK_BookingSeries_BookingSeries_ReplacedSeriesId",
                        column: x => x.ReplacedSeriesId,
                        principalTable: "BookingSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingSeries_Courts_CourtId",
                        column: x => x.CourtId,
                        principalTable: "Courts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingSeries_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeriesMisses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SeriesId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Refusal = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    NoticedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesMisses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeriesMisses_BookingSeries_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "BookingSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_SeriesId",
                table: "Bookings",
                column: "SeriesId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Bookings_BookerOrCustomer",
                table: "Bookings",
                sql: "(\"Channel\" = 1 AND \"BookerUserId\" IS NOT NULL AND \"CustomerName\" IS NULL) OR (\"Channel\" = 2 AND \"BookerUserId\" IS NULL AND \"CustomerName\" IS NOT NULL AND (\"PaidAtCounter\" IS NOT NULL OR \"SeriesId\" IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSeries_CourtId",
                table: "BookingSeries",
                column: "CourtId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSeries_ReplacedSeriesId",
                table: "BookingSeries",
                column: "ReplacedSeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSeries_VenueId_CourtId_Day_FromHour_UntilHour",
                table: "BookingSeries",
                columns: new[] { "VenueId", "CourtId", "Day", "FromHour", "UntilHour" },
                unique: true,
                filter: "\"State\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSeries_VenueId_State_CreatedAt",
                table: "BookingSeries",
                columns: new[] { "VenueId", "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesMisses_SeriesId_Date",
                table: "SeriesMisses",
                columns: new[] { "SeriesId", "Date" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_BookingSeries_SeriesId",
                table: "Bookings",
                column: "SeriesId",
                principalTable: "BookingSeries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // A week the venue has been told it could not have stays told: the row is history, so
            // the database refuses to rewrite it — the same pair of triggers every other history
            // table carries (migration AuditRowsArePermanent, CLAUDE.md).
            migrationBuilder.Sql("""
                CREATE TRIGGER "SeriesMisses_permanent"
                    BEFORE UPDATE OR DELETE ON "SeriesMisses"
                    FOR EACH ROW EXECUTE FUNCTION audit_rows_are_permanent();

                CREATE TRIGGER "SeriesMisses_not_truncated"
                    BEFORE TRUNCATE ON "SeriesMisses"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_rows_are_permanent();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "SeriesMisses_permanent" ON "SeriesMisses";
                DROP TRIGGER IF EXISTS "SeriesMisses_not_truncated" ON "SeriesMisses";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_BookingSeries_SeriesId",
                table: "Bookings");

            migrationBuilder.DropTable(
                name: "SeriesMisses");

            migrationBuilder.DropTable(
                name: "BookingSeries");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_SeriesId",
                table: "Bookings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Bookings_BookerOrCustomer",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "Bookings");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Bookings_BookerOrCustomer",
                table: "Bookings",
                sql: "(\"Channel\" = 1 AND \"BookerUserId\" IS NOT NULL AND \"CustomerName\" IS NULL) OR (\"Channel\" = 2 AND \"BookerUserId\" IS NULL AND \"CustomerName\" IS NOT NULL AND \"PaidAtCounter\" IS NOT NULL)");
        }
    }
}
