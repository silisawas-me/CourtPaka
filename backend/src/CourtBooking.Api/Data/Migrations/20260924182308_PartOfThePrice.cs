using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartOfThePrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DepositPercent",
                table: "Venues",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<decimal>(
                name: "DepositBaht",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Every booking made before this column existed was held on the whole of its price,
            // and nothing else would say so: left at nought, a cancellation would give back
            // nothing and the desk would be told to collect a price that had already arrived.
            migrationBuilder.Sql(@"UPDATE ""Bookings"" SET ""DepositBaht"" = ""TotalBaht"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DepositPercent",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "DepositBaht",
                table: "Bookings");
        }
    }
}
