using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MembershipChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MembershipChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    PermissionsBefore = table.Column<int>(type: "integer", nullable: true),
                    PermissionsAfter = table.Column<int>(type: "integer", nullable: true),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MembershipChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MembershipChanges_AspNetUsers_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MembershipChanges_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MembershipChanges_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipChanges_ChangedByUserId",
                table: "MembershipChanges",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipChanges_UserId",
                table: "MembershipChanges",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipChanges_VenueId_ChangedAt",
                table: "MembershipChanges",
                columns: new[] { "VenueId", "ChangedAt" });

            // Append-only like every history table (AuditRowsArePermanent), in the migration that
            // makes the table, so there is no window in which it can be rewritten (PRD 8).
            migrationBuilder.Sql(@"
                CREATE TRIGGER ""MembershipChanges_permanent""
                    BEFORE UPDATE OR DELETE ON ""MembershipChanges""
                    FOR EACH ROW EXECUTE FUNCTION audit_rows_are_permanent();

                CREATE TRIGGER ""MembershipChanges_not_truncated""
                    BEFORE TRUNCATE ON ""MembershipChanges""
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_rows_are_permanent();
                ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MembershipChanges");
        }
    }
}
