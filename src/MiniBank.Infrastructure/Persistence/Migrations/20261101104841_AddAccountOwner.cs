using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniBank.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000a1"),
                column: "user_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000a2"),
                column: "user_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000a3"),
                column: "user_id",
                value: null);

            migrationBuilder.CreateIndex(
                name: "ix_accounts_user_id",
                table: "accounts",
                column: "user_id");

            // NOT VALID: уже существующие счета без владельца остаются, а новые без владельца база не примет.
            migrationBuilder.Sql(
                "ALTER TABLE accounts ADD CONSTRAINT ck_accounts_owner_required CHECK (is_system OR user_id IS NOT NULL) NOT VALID");

            migrationBuilder.AddForeignKey(
                name: "fk_accounts_users_user_id",
                table: "accounts",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_accounts_users_user_id",
                table: "accounts");

            migrationBuilder.DropIndex(
                name: "ix_accounts_user_id",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_owner_required",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "accounts");
        }
    }
}
