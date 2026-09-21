using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <summary>
    /// Every venue's standing starts with the row that says it applied (VenueStatusChange: "null
    /// on the first row, which records the venue applying"). Applications were not written until
    /// now, so each venue without one gets it, dated when the venue was created (PRD US-10, US-20).
    /// Resubmissions made before now cannot be recovered and are not invented.
    /// </summary>
    public partial class VenueApplicationsRecorded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "VenueStatusChanges" ("Id", "VenueId", "From", "To", "ChangedAt", "ChangedByUserId", "Reason")
                SELECT gen_random_uuid(), v."Id", NULL, 1, v."CreatedAt", NULL, NULL
                FROM "Venues" v
                WHERE NOT EXISTS (
                    SELECT 1 FROM "VenueStatusChanges" c
                    WHERE c."VenueId" = v."Id" AND c."From" IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // History is append-only (AuditRowsArePermanent): the rows stay.
        }
    }
}
