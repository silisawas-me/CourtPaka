using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CounterBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "BookerUserId",
                table: "Bookings",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "CustomerName",
                table: "Bookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerPhone",
                table: "Bookings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaidAtCounter",
                table: "Bookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Bookings_BookerOrCustomer",
                table: "Bookings",
                sql: "(\"Channel\" = 1 AND \"BookerUserId\" IS NOT NULL AND \"CustomerName\" IS NULL) OR (\"Channel\" = 2 AND \"BookerUserId\" IS NULL AND \"CustomerName\" IS NOT NULL AND \"PaidAtCounter\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A counter booking has nobody to point BookerUserId at, so there is no schema without
            // the column being nullable that can still hold it. Refuse rather than delete bookings
            // (and their audit rows) to make the column fit; roll back the app image instead.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Bookings" WHERE "BookerUserId" IS NULL) THEN
                        RAISE EXCEPTION 'CounterBookings cannot be rolled back: counter bookings exist. Redeploy the previous image and keep this schema.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Bookings_BookerOrCustomer",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CustomerName",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CustomerPhone",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PaidAtCounter",
                table: "Bookings");

            migrationBuilder.AlterColumn<Guid>(
                name: "BookerUserId",
                table: "Bookings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
