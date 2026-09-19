using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PricesAndCancellationPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CancellationPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CancellationPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CancellationPolicies_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceLists_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CancellationTiers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoursBefore = table.Column<int>(type: "integer", nullable: false),
                    RefundPercent = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CancellationTiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CancellationTiers_CancellationPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "CancellationPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceBands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PriceListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<int>(type: "integer", nullable: false),
                    FromHour = table.Column<int>(type: "integer", nullable: false),
                    ToHour = table.Column<int>(type: "integer", nullable: false),
                    BahtPerHour = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceBands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceBands_PriceLists_PriceListId",
                        column: x => x.PriceListId,
                        principalTable: "PriceLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CancellationPolicies_VenueId_CreatedAt",
                table: "CancellationPolicies",
                columns: new[] { "VenueId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CancellationTiers_PolicyId_HoursBefore",
                table: "CancellationTiers",
                columns: new[] { "PolicyId", "HoursBefore" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceBands_PriceListId_Day_FromHour",
                table: "PriceBands",
                columns: new[] { "PriceListId", "Day", "FromHour" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_VenueId_CreatedAt",
                table: "PriceLists",
                columns: new[] { "VenueId", "CreatedAt" });

            // Every venue already operates under the default terms (PRD S-11); this makes that a
            // row, so "the policy in force" is one thing to read rather than a special case.
            migrationBuilder.Sql(
                """
                WITH new_policy AS (
                    INSERT INTO "CancellationPolicies" ("Id", "VenueId", "CreatedByUserId", "CreatedAt")
                    SELECT gen_random_uuid(), v."Id", m."UserId", now()
                    FROM "Venues" v
                    JOIN LATERAL (
                        SELECT "UserId" FROM "VenueMemberships"
                        WHERE "VenueId" = v."Id" AND "Role" = 1
                        ORDER BY "CreatedAt" LIMIT 1
                    ) m ON TRUE
                    RETURNING "Id"
                )
                INSERT INTO "CancellationTiers" ("Id", "PolicyId", "HoursBefore", "RefundPercent")
                SELECT gen_random_uuid(), "Id", 24, 100 FROM new_policy;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CancellationTiers");

            migrationBuilder.DropTable(
                name: "PriceBands");

            migrationBuilder.DropTable(
                name: "CancellationPolicies");

            migrationBuilder.DropTable(
                name: "PriceLists");
        }
    }
}
