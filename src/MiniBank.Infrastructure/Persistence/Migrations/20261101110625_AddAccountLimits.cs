using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniBank.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "daily_limit",
                table: "accounts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "per_operation_limit",
                table: "accounts",
                type: "bigint",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000a1"),
                columns: new[] { "daily_limit", "per_operation_limit" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000a2"),
                columns: new[] { "daily_limit", "per_operation_limit" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000a3"),
                columns: new[] { "daily_limit", "per_operation_limit" },
                values: new object[] { null, null });

            migrationBuilder.AddCheckConstraint(
                name: "ck_accounts_daily_limit_positive",
                table: "accounts",
                sql: "daily_limit > 0");

            // NOT VALID: счета, открытые до лимитов, остаются без них, а новые без лимитов база не примет.
            migrationBuilder.Sql(
                "ALTER TABLE accounts ADD CONSTRAINT ck_accounts_limits_required " +
                "CHECK (is_system OR (per_operation_limit IS NOT NULL AND daily_limit IS NOT NULL)) NOT VALID");

            migrationBuilder.AddCheckConstraint(
                name: "ck_accounts_per_operation_limit_positive",
                table: "accounts",
                sql: "per_operation_limit > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_daily_limit_positive",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_limits_required",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_per_operation_limit_positive",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "daily_limit",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "per_operation_limit",
                table: "accounts");
        }
    }
}
