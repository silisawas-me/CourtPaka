using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoneyAfterTheTillIsCounted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentReceipts_VenueId_ReceivedAt",
                table: "PaymentReceipts");

            migrationBuilder.AddColumn<DateOnly>(
                name: "CountsOn",
                table: "PaymentReceipts",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            // Every receipt written so far counted in its own day, because there was no other
            // day to count it in. The trigger that makes this table permanent refuses an UPDATE,
            // so it is switched off for the length of this one statement — the rows are being
            // given the answer they already had, not a different one.
            //
            // The day is the venue's, not the reader's (PRD BR-10), so the conversion names the
            // timezone rather than trusting the server's.
            migrationBuilder.Sql("""
                ALTER TABLE "PaymentReceipts" DISABLE TRIGGER "PaymentReceipts_permanent";

                UPDATE "PaymentReceipts"
                SET "CountsOn" = ("ReceivedAt" AT TIME ZONE 'Asia/Bangkok')::date;

                ALTER TABLE "PaymentReceipts" ENABLE TRIGGER "PaymentReceipts_permanent";
                """);

            // The column carries its own value from here on; a default would quietly write the
            // wrong day for anything that forgot to set it.
            migrationBuilder.Sql(
                """ALTER TABLE "PaymentReceipts" ALTER COLUMN "CountsOn" DROP DEFAULT;""");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_VenueId_CountsOn",
                table: "PaymentReceipts",
                columns: new[] { "VenueId", "CountsOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentReceipts_VenueId_CountsOn",
                table: "PaymentReceipts");

            migrationBuilder.DropColumn(
                name: "CountsOn",
                table: "PaymentReceipts");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_VenueId_ReceivedAt",
                table: "PaymentReceipts",
                columns: new[] { "VenueId", "ReceivedAt" });
        }
    }
}
