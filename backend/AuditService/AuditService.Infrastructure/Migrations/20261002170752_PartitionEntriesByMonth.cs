using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PartitionEntriesByMonth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_entries",
                schema: "audit",
                table: "entries");

            migrationBuilder.DropIndex(
                name: "IX_entries_MessageId",
                schema: "audit",
                table: "entries");

            migrationBuilder.AddPrimaryKey(
                name: "PK_entries",
                schema: "audit",
                table: "entries",
                columns: new[] { "Id", "OccurredAt" });

            migrationBuilder.CreateTable(
                name: "recorded_messages",
                schema: "audit",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recorded_messages", x => x.MessageId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_entries_MessageId",
                schema: "audit",
                table: "entries",
                column: "MessageId");

            // ---- Not generated: EF cannot express a partitioned table, so the table is rebuilt as one (ADR 0027). ----
            // The generated operations above gave the plain table its new key and created recorded_messages; what follows
            // backfills it, moves the rows into a table partitioned by month (UTC), and drops the old one. It takes an
            // exclusive lock on audit.entries for the length of the copy: fine at this size, the thing to plan around on a
            // large log.
            migrationBuilder.Sql("""
                INSERT INTO audit.recorded_messages ("MessageId", "RecordedAt")
                SELECT "MessageId", min("ReceivedAt") FROM audit.entries GROUP BY "MessageId";

                ALTER TABLE audit.entries RENAME TO entries_unpartitioned;
                ALTER TABLE audit.entries_unpartitioned RENAME CONSTRAINT "PK_entries" TO "PK_entries_unpartitioned";
                ALTER INDEX audit."IX_entries_AggregateId" RENAME TO "IX_entries_AggregateId_unpartitioned";
                ALTER INDEX audit."IX_entries_MessageId" RENAME TO "IX_entries_MessageId_unpartitioned";
                ALTER INDEX audit."IX_entries_OccurredAt" RENAME TO "IX_entries_OccurredAt_unpartitioned";

                CREATE TABLE audit.entries (LIKE audit.entries_unpartitioned INCLUDING DEFAULTS) PARTITION BY RANGE ("OccurredAt");
                ALTER TABLE audit.entries ADD CONSTRAINT "PK_entries" PRIMARY KEY ("Id", "OccurredAt");
                CREATE INDEX "IX_entries_AggregateId" ON audit.entries ("AggregateId");
                CREATE INDEX "IX_entries_MessageId" ON audit.entries ("MessageId");
                CREATE INDEX "IX_entries_OccurredAt" ON audit.entries ("OccurredAt");

                CREATE TABLE audit.entries_default PARTITION OF audit.entries DEFAULT;

                DO $$
                DECLARE
                    first_month timestamptz;
                    last_month timestamptz;
                    m timestamptz;
                BEGIN
                    SELECT date_trunc('month', coalesce(min("OccurredAt"), now()) AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
                      INTO first_month FROM audit.entries_unpartitioned;
                    last_month := date_trunc('month', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' + interval '3 months';
                    m := first_month;
                    WHILE m <= last_month LOOP
                        EXECUTE format(
                            'CREATE TABLE audit.%I PARTITION OF audit.entries FOR VALUES FROM (%L) TO (%L)',
                            'entries_y' || to_char(m AT TIME ZONE 'UTC', 'YYYY') || 'm' || to_char(m AT TIME ZONE 'UTC', 'MM'),
                            m, m + interval '1 month');
                        m := m + interval '1 month';
                    END LOOP;
                END $$;

                INSERT INTO audit.entries SELECT * FROM audit.entries_unpartitioned;
                DROP TABLE audit.entries_unpartitioned;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to one plain table first (the generated operations below expect it), then undo the key change.
            migrationBuilder.Sql("""
                CREATE TABLE audit.entries_plain (LIKE audit.entries INCLUDING DEFAULTS);
                INSERT INTO audit.entries_plain SELECT * FROM audit.entries;
                DROP TABLE audit.entries;
                ALTER TABLE audit.entries_plain RENAME TO entries;
                ALTER TABLE audit.entries ADD CONSTRAINT "PK_entries" PRIMARY KEY ("Id", "OccurredAt");
                CREATE INDEX "IX_entries_AggregateId" ON audit.entries ("AggregateId");
                CREATE INDEX "IX_entries_MessageId" ON audit.entries ("MessageId");
                CREATE INDEX "IX_entries_OccurredAt" ON audit.entries ("OccurredAt");
                """);

            migrationBuilder.DropTable(
                name: "recorded_messages",
                schema: "audit");

            migrationBuilder.DropPrimaryKey(
                name: "PK_entries",
                schema: "audit",
                table: "entries");

            migrationBuilder.DropIndex(
                name: "IX_entries_MessageId",
                schema: "audit",
                table: "entries");

            migrationBuilder.AddPrimaryKey(
                name: "PK_entries",
                schema: "audit",
                table: "entries",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_entries_MessageId",
                schema: "audit",
                table: "entries",
                column: "MessageId",
                unique: true);
        }
    }
}
