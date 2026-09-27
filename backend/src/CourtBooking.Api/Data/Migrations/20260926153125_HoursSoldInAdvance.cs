using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class HoursSoldInAdvance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Bookings_BookerOrCustomer",
                table: "Bookings");

            migrationBuilder.AlterColumn<Guid>(
                name: "BookingId",
                table: "PaymentReceipts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "PackageId",
                table: "PaymentReceipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PackageBaht",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "PackageHours",
                table: "Bookings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PackageId",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PackageTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Hours = table.Column<int>(type: "integer", nullable: false),
                    PriceBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    ValidForDays = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WithdrawnByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageTypes", x => x.Id);
                    table.CheckConstraint("CK_PackageTypes_IsAnOffer", "\"Hours\" > 0 AND \"Hours\" <= 200 AND \"PriceBaht\" > 0 AND \"ValidForDays\" > 0 AND \"ValidForDays\" <= 730");
                    table.ForeignKey(
                        name: "FK_PackageTypes_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HourPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CustomerPhone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    HoursSold = table.Column<int>(type: "integer", nullable: false),
                    PriceBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: false),
                    SoldAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SoldByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HourPackages", x => x.Id);
                    table.CheckConstraint("CK_HourPackages_WasSold", "\"HoursSold\" > 0 AND \"PriceBaht\" > 0");
                    table.ForeignKey(
                        name: "FK_HourPackages_PackageTypes_PackageTypeId",
                        column: x => x.PackageTypeId,
                        principalTable: "PackageTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HourPackages_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hours = table.Column<int>(type: "integer", nullable: false),
                    Move = table.Column<int>(type: "integer", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageEntries", x => x.Id);
                    table.CheckConstraint("CK_PackageEntries_HoursMoved", "\"Hours\" <> 0");
                    table.ForeignKey(
                        name: "FK_PackageEntries_AspNetUsers_ByUserId",
                        column: x => x.ByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PackageEntries_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PackageEntries_HourPackages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "HourPackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_PackageId",
                table: "PaymentReceipts",
                column: "PackageId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentReceipts_ForOneThing",
                table: "PaymentReceipts",
                sql: "(\"BookingId\" IS NULL) <> (\"PackageId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_PackageId",
                table: "Bookings",
                column: "PackageId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Bookings_BookerOrCustomer",
                table: "Bookings",
                sql: "(\"Channel\" = 1 AND \"BookerUserId\" IS NOT NULL AND \"CustomerName\" IS NULL) OR (\"Channel\" = 2 AND \"BookerUserId\" IS NULL AND \"CustomerName\" IS NOT NULL AND (\"PaidAtCounter\" IS NOT NULL OR \"SeriesId\" IS NOT NULL OR \"PackageId\" IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Bookings_PackagePaysForItWhole",
                table: "Bookings",
                sql: "(\"PackageId\" IS NULL AND \"PackageHours\" = 0 AND \"PackageBaht\" = 0) OR (\"PackageId\" IS NOT NULL AND \"PackageHours\" > 0 AND \"PackageBaht\" >= 0)");

            migrationBuilder.CreateIndex(
                name: "IX_HourPackages_PackageTypeId",
                table: "HourPackages",
                column: "PackageTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_HourPackages_VenueId_ExpiresOn_ExpiredAt",
                table: "HourPackages",
                columns: new[] { "VenueId", "ExpiresOn", "ExpiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HourPackages_VenueId_SoldAt",
                table: "HourPackages",
                columns: new[] { "VenueId", "SoldAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PackageEntries_BookingId",
                table: "PackageEntries",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageEntries_ByUserId",
                table: "PackageEntries",
                column: "ByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageEntries_PackageId_At",
                table: "PackageEntries",
                columns: new[] { "PackageId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PackageTypes_VenueId_WithdrawnAt_CreatedAt",
                table: "PackageTypes",
                columns: new[] { "VenueId", "WithdrawnAt", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_HourPackages_PackageId",
                table: "Bookings",
                column: "PackageId",
                principalTable: "HourPackages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentReceipts_HourPackages_PackageId",
                table: "PaymentReceipts",
                column: "PackageId",
                principalTable: "HourPackages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // The ledger is history: what a package has left is the sum of its rows, so a row that
            // could be rewritten is a balance that could be rewritten. The same pair of triggers
            // every other history table carries (migration AuditRowsArePermanent, CLAUDE.md).
            migrationBuilder.Sql("""
                CREATE TRIGGER "PackageEntries_permanent"
                    BEFORE UPDATE OR DELETE ON "PackageEntries"
                    FOR EACH ROW EXECUTE FUNCTION audit_rows_are_permanent();

                CREATE TRIGGER "PackageEntries_not_truncated"
                    BEFORE TRUNCATE ON "PackageEntries"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_rows_are_permanent();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "PackageEntries_permanent" ON "PackageEntries";
                DROP TRIGGER IF EXISTS "PackageEntries_not_truncated" ON "PackageEntries";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_HourPackages_PackageId",
                table: "Bookings");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentReceipts_HourPackages_PackageId",
                table: "PaymentReceipts");

            migrationBuilder.DropTable(
                name: "PackageEntries");

            migrationBuilder.DropTable(
                name: "HourPackages");

            migrationBuilder.DropTable(
                name: "PackageTypes");

            migrationBuilder.DropIndex(
                name: "IX_PaymentReceipts_PackageId",
                table: "PaymentReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentReceipts_ForOneThing",
                table: "PaymentReceipts");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_PackageId",
                table: "Bookings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Bookings_BookerOrCustomer",
                table: "Bookings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Bookings_PackagePaysForItWhole",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PackageId",
                table: "PaymentReceipts");

            migrationBuilder.DropColumn(
                name: "PackageBaht",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PackageHours",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PackageId",
                table: "Bookings");

            migrationBuilder.AlterColumn<Guid>(
                name: "BookingId",
                table: "PaymentReceipts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Bookings_BookerOrCustomer",
                table: "Bookings",
                sql: "(\"Channel\" = 1 AND \"BookerUserId\" IS NOT NULL AND \"CustomerName\" IS NULL) OR (\"Channel\" = 2 AND \"BookerUserId\" IS NULL AND \"CustomerName\" IS NOT NULL AND (\"PaidAtCounter\" IS NOT NULL OR \"SeriesId\" IS NOT NULL))");
        }
    }
}
