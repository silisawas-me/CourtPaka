using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CancellationCause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Cause",
                table: "BookingStatusChanges",
                type: "integer",
                nullable: true);

            // Every reason recorded so far lives in the text as a prefix, which is exactly
            // what this column exists to replace. Move them across rather than leave the rows
            // before today uncountable, and take the prefix back out of the note.
            migrationBuilder.Sql(@"
                UPDATE ""BookingStatusChanges""
                SET ""Cause"" = CASE
                    WHEN ""Reason"" = 'CustomerRequest' OR ""Reason"" LIKE 'CustomerRequest: %' THEN 1
                    WHEN ""Reason"" = 'VenueInitiated' OR ""Reason"" LIKE 'VenueInitiated: %' THEN 2
                    WHEN ""Reason"" = 'PaymentNotReceived' OR ""Reason"" LIKE 'PaymentNotReceived: %' THEN 3
                    END,
                    ""Reason"" = CASE
                        WHEN position(': ' in ""Reason"") > 0
                            THEN substring(""Reason"" from position(': ' in ""Reason"") + 2)
                        ELSE ''
                    END
                WHERE ""Reason"" = 'CustomerRequest'
                   OR ""Reason"" LIKE 'CustomerRequest: %'
                   OR ""Reason"" = 'VenueInitiated'
                   OR ""Reason"" LIKE 'VenueInitiated: %'
                   OR ""Reason"" = 'PaymentNotReceived'
                   OR ""Reason"" LIKE 'PaymentNotReceived: %';
            ");

            migrationBuilder.CreateIndex(
                name: "IX_BookingStatusChanges_Cause",
                table: "BookingStatusChanges",
                column: "Cause",
                filter: "\"Cause\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Put the reason back where it was, so dropping the column loses nothing.
            migrationBuilder.Sql(@"
                UPDATE ""BookingStatusChanges""
                SET ""Reason"" = CASE ""Cause""
                        WHEN 1 THEN 'CustomerRequest'
                        WHEN 2 THEN 'VenueInitiated'
                        WHEN 3 THEN 'PaymentNotReceived'
                    END || CASE
                        WHEN coalesce(""Reason"", '') = '' THEN ''
                        ELSE ': ' || ""Reason""
                    END
                WHERE ""Cause"" IS NOT NULL;
            ");

            migrationBuilder.DropIndex(
                name: "IX_BookingStatusChanges_Cause",
                table: "BookingStatusChanges");

            migrationBuilder.DropColumn(
                name: "Cause",
                table: "BookingStatusChanges");
        }
    }
}
