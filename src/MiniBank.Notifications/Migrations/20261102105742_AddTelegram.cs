using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniBank.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "link_codes",
                schema: "notifications",
                columns: table => new
                {
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_codes", x => x.code_hash);
                });

            migrationBuilder.CreateTable(
                name: "notification_settings",
                schema: "notifications",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    on_transfer_sent = table.Column<bool>(type: "boolean", nullable: false),
                    on_transfer_received = table.Column<bool>(type: "boolean", nullable: false),
                    on_deposit = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_settings", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "telegram_deliveries",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telegram_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_telegram_deliveries_received_events_received_event_id",
                        column: x => x.received_event_id,
                        principalSchema: "notifications",
                        principalTable: "received_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "telegram_links",
                schema: "notifications",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telegram_links", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_link_codes_user_id",
                schema: "notifications",
                table: "link_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_telegram_deliveries_pending",
                schema: "notifications",
                table: "telegram_deliveries",
                column: "next_attempt_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_telegram_deliveries_received_event_id",
                schema: "notifications",
                table: "telegram_deliveries",
                column: "received_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_telegram_links_chat_id",
                schema: "notifications",
                table: "telegram_links",
                column: "chat_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "link_codes",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "notification_settings",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "telegram_deliveries",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "telegram_links",
                schema: "notifications");
        }
    }
}
