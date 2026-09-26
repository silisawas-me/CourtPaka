using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WhatTheCounterAlsoSells : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentReceipts_ForOneThing",
                table: "PaymentReceipts");

            migrationBuilder.AddColumn<Guid>(
                name: "SaleId",
                table: "PaymentReceipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShopItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PriceBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Unit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Counted = table.Column<bool>(type: "boolean", nullable: false),
                    TellMeAt = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WithdrawnByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopItems", x => x.Id);
                    table.CheckConstraint("CK_ShopItems_IsSomethingToSell", "\"PriceBaht\" > 0 AND (\"TellMeAt\" IS NULL OR \"TellMeAt\" >= 0)");
                    table.ForeignKey(
                        name: "FK_ShopItems_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopSales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    TotalBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    SoldAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SoldByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopSales", x => x.Id);
                    table.CheckConstraint("CK_ShopSales_CameToSomething", "\"TotalBaht\" > 0");
                    table.ForeignKey(
                        name: "FK_ShopSales_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShopSales_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Spends",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AmountBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PaidBy = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    VoidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VoidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Spends", x => x.Id);
                    table.CheckConstraint("CK_Spends_IsMoney", "\"AmountBaht\" > 0");
                    table.ForeignKey(
                        name: "FK_Spends_AspNetUsers_RecordedByUserId",
                        column: x => x.RecordedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Spends_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopSaleLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    EachBaht = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopSaleLines", x => x.Id);
                    table.CheckConstraint("CK_ShopSaleLines_IsALine", "\"Quantity\" > 0 AND \"Quantity\" <= 100 AND \"EachBaht\" > 0");
                    table.ForeignKey(
                        name: "FK_ShopSaleLines_ShopItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "ShopItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShopSaleLines_ShopSales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "ShopSales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Move = table.Column<int>(type: "integer", nullable: false),
                    SaleId = table.Column<Guid>(type: "uuid", nullable: true),
                    SpendId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockEntries", x => x.Id);
                    table.CheckConstraint("CK_StockEntries_SomethingMoved", "\"Quantity\" <> 0");
                    table.ForeignKey(
                        name: "FK_StockEntries_AspNetUsers_ByUserId",
                        column: x => x.ByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockEntries_ShopItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "ShopItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StockEntries_ShopSales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "ShopSales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_SaleId",
                table: "PaymentReceipts",
                column: "SaleId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentReceipts_ForOneThing",
                table: "PaymentReceipts",
                sql: "(CASE WHEN \"BookingId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"PackageId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"SaleId\" IS NULL THEN 0 ELSE 1 END) = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ShopItems_VenueId_WithdrawnAt_Name",
                table: "ShopItems",
                columns: new[] { "VenueId", "WithdrawnAt", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopSaleLines_ItemId",
                table: "ShopSaleLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopSaleLines_SaleId",
                table: "ShopSaleLines",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopSales_BookingId",
                table: "ShopSales",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopSales_VenueId_SoldAt",
                table: "ShopSales",
                columns: new[] { "VenueId", "SoldAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Spends_RecordedByUserId",
                table: "Spends",
                column: "RecordedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Spends_VenueId_PaidOn",
                table: "Spends",
                columns: new[] { "VenueId", "PaidOn" });

            migrationBuilder.CreateIndex(
                name: "IX_StockEntries_ByUserId",
                table: "StockEntries",
                column: "ByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockEntries_ItemId_At",
                table: "StockEntries",
                columns: new[] { "ItemId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_StockEntries_SaleId",
                table: "StockEntries",
                column: "SaleId");

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentReceipts_ShopSales_SaleId",
                table: "PaymentReceipts",
                column: "SaleId",
                principalTable: "ShopSales",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // Two ledgers that are only ever added to: what a venue has on the shelf and what it
            // paid out are both sums of rows, so a row that could be rewritten is a balance that
            // could be rewritten. A spend is voided rather than changed, the way a record of
            // money sent back is (migration AuditRowsArePermanent, CLAUDE.md, PRD US-18).
            migrationBuilder.Sql("""
                CREATE TRIGGER "StockEntries_permanent"
                    BEFORE UPDATE OR DELETE ON "StockEntries"
                    FOR EACH ROW EXECUTE FUNCTION audit_rows_are_permanent();

                CREATE TRIGGER "StockEntries_not_truncated"
                    BEFORE TRUNCATE ON "StockEntries"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_rows_are_permanent();

                CREATE FUNCTION spends_are_only_voided() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Spends are permanent; void one instead.';
                    END IF;

                    IF OLD."VoidedAt" IS NOT NULL
                        OR NEW."Id" IS DISTINCT FROM OLD."Id"
                        OR NEW."VenueId" IS DISTINCT FROM OLD."VenueId"
                        OR NEW."Kind" IS DISTINCT FROM OLD."Kind"
                        OR NEW."AmountBaht" IS DISTINCT FROM OLD."AmountBaht"
                        OR NEW."PaidOn" IS DISTINCT FROM OLD."PaidOn"
                        OR NEW."PaidBy" IS DISTINCT FROM OLD."PaidBy"
                        OR NEW."Note" IS DISTINCT FROM OLD."Note"
                        OR NEW."RecordedAt" IS DISTINCT FROM OLD."RecordedAt"
                        OR NEW."RecordedByUserId" IS DISTINCT FROM OLD."RecordedByUserId"
                    THEN
                        RAISE EXCEPTION 'A spend can only be voided, once.';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "Spends_only_voided"
                    BEFORE UPDATE OR DELETE ON "Spends"
                    FOR EACH ROW EXECUTE FUNCTION spends_are_only_voided();

                CREATE TRIGGER "Spends_not_truncated"
                    BEFORE TRUNCATE ON "Spends"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_rows_are_permanent();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "StockEntries_permanent" ON "StockEntries";
                DROP TRIGGER IF EXISTS "StockEntries_not_truncated" ON "StockEntries";
                DROP TRIGGER IF EXISTS "Spends_only_voided" ON "Spends";
                DROP TRIGGER IF EXISTS "Spends_not_truncated" ON "Spends";
                DROP FUNCTION IF EXISTS spends_are_only_voided();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentReceipts_ShopSales_SaleId",
                table: "PaymentReceipts");

            migrationBuilder.DropTable(
                name: "ShopSaleLines");

            migrationBuilder.DropTable(
                name: "Spends");

            migrationBuilder.DropTable(
                name: "StockEntries");

            migrationBuilder.DropTable(
                name: "ShopItems");

            migrationBuilder.DropTable(
                name: "ShopSales");

            migrationBuilder.DropIndex(
                name: "IX_PaymentReceipts_SaleId",
                table: "PaymentReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentReceipts_ForOneThing",
                table: "PaymentReceipts");

            migrationBuilder.DropColumn(
                name: "SaleId",
                table: "PaymentReceipts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentReceipts_ForOneThing",
                table: "PaymentReceipts",
                sql: "(\"BookingId\" IS NULL) <> (\"PackageId\" IS NULL)");
        }
    }
}
