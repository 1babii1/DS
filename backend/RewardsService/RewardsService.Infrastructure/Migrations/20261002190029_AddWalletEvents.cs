using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RewardsService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wallet_events",
                schema: "rewards",
                columns: table => new
                {
                    StreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_events", x => new { x.StreamId, x.Version });
                });

            // ---- Not generated: the history that already exists becomes the first events (ADR 0031). ----
            // One event per ledger row, versioned per wallet in the order the rows were written, carrying everything the
            // ledger row holds so that it can be rebuilt. Then each wallet's balance is set to what that history says: before
            // this, overlapping grants to one wallet could overwrite each other's balance, so a stored balance may be short of
            // its ledger; this makes the two agree.
            migrationBuilder.Sql("""
                INSERT INTO rewards.wallet_events ("StreamId", "Version", "EventType", "Data", "OccurredAt")
                SELECT "EmployeeId",
                       row_number() OVER (PARTITION BY "EmployeeId" ORDER BY "CreatedAt", "Id"),
                       'WalletAdjusted',
                       jsonb_build_object(
                           'TransactionId', "Id", 'Amount', "Amount", 'Reason', "Reason",
                           'Source', "Source", 'GrantedByAccountId', "GrantedByAccountId"),
                       "CreatedAt"
                FROM rewards.transactions;

                UPDATE rewards.wallets w
                SET "Balance" = h.total
                FROM (SELECT "EmployeeId", sum("Amount") AS total FROM rewards.transactions GROUP BY "EmployeeId") h
                WHERE w."EmployeeId" = h."EmployeeId" AND w."Balance" <> h.total;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wallet_events",
                schema: "rewards");
        }
    }
}
