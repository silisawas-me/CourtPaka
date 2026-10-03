using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CourtBooking.Api.Data.Migrations
{
    /// <summary>
    /// The history tables become append-only in the database, not only by agreement (PRD 8).
    ///
    /// They are what an admin reads to settle a complaint (US-22) and what a venue or a booker is
    /// shown when they dispute something; evidence that one bad write, or one careless hand at a
    /// psql prompt, can rewrite is not evidence. A refund record may be voided once and nothing
    /// else — that is the one change the product itself makes to it (US-18).
    /// </summary>
    public partial class AuditRowsArePermanent : Migration
    {
        private static readonly string[] AppendOnly =
        [
            "BookingStatusChanges",
            "VenueStatusChanges",
            "AccountStatusChanges",
            "CourtStatusChanges",
            "UserConsents",
            "SlipViewings",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_rows_are_permanent() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Rows of % are only ever added (%).', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'P0001';
                END $$;

                CREATE FUNCTION refund_record_only_voids() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP <> 'UPDATE' THEN
                        RAISE EXCEPTION 'Rows of RefundRecords are only ever added (%).', TG_OP
                            USING ERRCODE = 'P0001';
                    END IF;

                    -- Voided once, by somebody, with a reason; everything recorded before stays.
                    IF OLD."VoidedAt" IS NOT NULL
                        OR NEW."VoidedAt" IS NULL
                        OR NEW."VoidedByUserId" IS NULL
                        OR NEW."VoidReason" IS NULL
                        OR (NEW."Id", NEW."BookingId", NEW."AmountBaht", NEW."RefundedOn",
                            NEW."Method", NEW."Note", NEW."RecordedByUserId", NEW."RecordedAt")
                           IS DISTINCT FROM
                           (OLD."Id", OLD."BookingId", OLD."AmountBaht", OLD."RefundedOn",
                            OLD."Method", OLD."Note", OLD."RecordedByUserId", OLD."RecordedAt")
                    THEN
                        RAISE EXCEPTION 'A refund record can only be voided, once.'
                            USING ERRCODE = 'P0001';
                    END IF;

                    RETURN NEW;
                END $$;

                CREATE TRIGGER "RefundRecords_only_voids"
                    BEFORE UPDATE OR DELETE ON "RefundRecords"
                    FOR EACH ROW EXECUTE FUNCTION refund_record_only_voids();

                CREATE TRIGGER "RefundRecords_not_truncated"
                    BEFORE TRUNCATE ON "RefundRecords"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_rows_are_permanent();
                """);

            foreach (var table in AppendOnly)
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER "{table}_permanent"
                        BEFORE UPDATE OR DELETE ON "{table}"
                        FOR EACH ROW EXECUTE FUNCTION audit_rows_are_permanent();

                    CREATE TRIGGER "{table}_not_truncated"
                        BEFORE TRUNCATE ON "{table}"
                        FOR EACH STATEMENT EXECUTE FUNCTION audit_rows_are_permanent();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in AppendOnly)
            {
                migrationBuilder.Sql($"""
                    DROP TRIGGER "{table}_permanent" ON "{table}";
                    DROP TRIGGER "{table}_not_truncated" ON "{table}";
                    """);
            }

            migrationBuilder.Sql("""
                DROP TRIGGER "RefundRecords_only_voids" ON "RefundRecords";
                DROP TRIGGER "RefundRecords_not_truncated" ON "RefundRecords";
                DROP FUNCTION refund_record_only_voids();
                DROP FUNCTION audit_rows_are_permanent();
                """);
        }
    }
}
