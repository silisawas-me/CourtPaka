using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CloseByShift : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DailyClosings_VenueId_Date",
                table: "DailyClosings");

            migrationBuilder.AddColumn<bool>(
                name: "EndsDay",
                table: "DailyClosings",
                type: "boolean",
                nullable: false,
                // Every count before shifts closed its day.
                defaultValue: true);

            // The model writes the value every time; the default was only for the rows above.
            migrationBuilder.Sql("ALTER TABLE \"DailyClosings\" ALTER COLUMN \"EndsDay\" DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "IX_DailyClosings_VenueId_Date_ClosedAt",
                table: "DailyClosings",
                columns: new[] { "VenueId", "Date", "ClosedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyClosings_VenueId_Date_EndsDay",
                table: "DailyClosings",
                columns: new[] { "VenueId", "Date" },
                unique: true,
                filter: "\"EndsDay\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DailyClosings_VenueId_Date_ClosedAt",
                table: "DailyClosings");

            migrationBuilder.DropIndex(
                name: "IX_DailyClosings_VenueId_Date_EndsDay",
                table: "DailyClosings");

            // A day had one count before shifts: going back keeps the close and drops the shifts.
            migrationBuilder.Sql("DELETE FROM \"DailyClosings\" WHERE NOT \"EndsDay\";");

            migrationBuilder.DropColumn(
                name: "EndsDay",
                table: "DailyClosings");

            migrationBuilder.CreateIndex(
                name: "IX_DailyClosings_VenueId_Date",
                table: "DailyClosings",
                columns: new[] { "VenueId", "Date" },
                unique: true);
        }
    }
}
