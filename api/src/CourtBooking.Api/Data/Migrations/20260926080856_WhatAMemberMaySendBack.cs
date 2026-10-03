using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WhatAMemberMaySendBack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RefundLimitBaht",
                table: "VenueMemberships",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RefundLimitAfter",
                table: "MembershipChanges",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RefundLimitBefore",
                table: "MembershipChanges",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RefundLimitBaht",
                table: "VenueMemberships");

            migrationBuilder.DropColumn(
                name: "RefundLimitAfter",
                table: "MembershipChanges");

            migrationBuilder.DropColumn(
                name: "RefundLimitBefore",
                table: "MembershipChanges");
        }
    }
}
