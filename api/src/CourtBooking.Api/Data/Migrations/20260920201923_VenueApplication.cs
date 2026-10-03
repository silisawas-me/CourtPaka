using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class VenueApplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgreementAcceptedAt",
                table: "Venues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgreementAcceptedByUserId",
                table: "Venues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgreementVersion",
                table: "Venues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Business_BillingAddress",
                table: "Venues",
                type: "character varying(400)",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Business_IsVatRegistered",
                table: "Venues",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "Business_Latitude",
                table: "Venues",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Business_LegalName",
                table: "Venues",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "Business_Longitude",
                table: "Venues",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Business_PromptPayAccountName",
                table: "Venues",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Business_PromptPayId",
                table: "Venues",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Business_TaxBranch",
                table: "Venues",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Business_TaxId",
                table: "Venues",
                type: "character varying(13)",
                maxLength: 13,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgreementAcceptedAt",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "AgreementAcceptedByUserId",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "AgreementVersion",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Business_BillingAddress",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Business_IsVatRegistered",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Business_Latitude",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Business_LegalName",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Business_Longitude",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Business_PromptPayAccountName",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Business_PromptPayId",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Business_TaxBranch",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "Business_TaxId",
                table: "Venues");
        }
    }
}
