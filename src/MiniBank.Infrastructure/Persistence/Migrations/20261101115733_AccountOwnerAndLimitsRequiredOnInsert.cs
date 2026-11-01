using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniBank.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Владелец и лимиты обязательны только у новых счетов. CHECK NOT VALID действует и на UPDATE старых строк,
    /// поэтому зачисление на счёт, открытый до миграций (меняется только баланс), падало с 500.
    /// Триггер BEFORE INSERT проверяет то же самое, но не трогает существующие строки.
    /// </summary>
    public partial class AccountOwnerAndLimitsRequiredOnInsert : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_limits_required",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_owner_required",
                table: "accounts");

            // Имена ограничений сохранены в CONSTRAINT: по ним приложение и тесты распознают причину отказа.
            migrationBuilder.Sql(
                "CREATE FUNCTION accounts_require_owner_and_limits() RETURNS trigger LANGUAGE plpgsql AS $$\n" +
                "BEGIN\n" +
                "    IF NOT NEW.is_system AND NEW.user_id IS NULL THEN\n" +
                "        RAISE EXCEPTION 'new row for relation \"accounts\" violates check constraint \"ck_accounts_owner_required\"'\n" +
                "            USING ERRCODE = '23514', CONSTRAINT = 'ck_accounts_owner_required', TABLE = 'accounts';\n" +
                "    END IF;\n" +
                "    IF NOT NEW.is_system AND (NEW.per_operation_limit IS NULL OR NEW.daily_limit IS NULL) THEN\n" +
                "        RAISE EXCEPTION 'new row for relation \"accounts\" violates check constraint \"ck_accounts_limits_required\"'\n" +
                "            USING ERRCODE = '23514', CONSTRAINT = 'ck_accounts_limits_required', TABLE = 'accounts';\n" +
                "    END IF;\n" +
                "    RETURN NEW;\n" +
                "END;\n" +
                "$$");

            migrationBuilder.Sql(
                "CREATE TRIGGER trg_accounts_require_owner_and_limits BEFORE INSERT ON accounts " +
                "FOR EACH ROW EXECUTE FUNCTION accounts_require_owner_and_limits()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_accounts_require_owner_and_limits ON accounts");
            migrationBuilder.Sql("DROP FUNCTION accounts_require_owner_and_limits()");

            migrationBuilder.Sql(
                "ALTER TABLE accounts ADD CONSTRAINT ck_accounts_owner_required CHECK (is_system OR user_id IS NOT NULL) NOT VALID");
            migrationBuilder.Sql(
                "ALTER TABLE accounts ADD CONSTRAINT ck_accounts_limits_required " +
                "CHECK (is_system OR (per_operation_limit IS NOT NULL AND daily_limit IS NOT NULL)) NOT VALID");
        }
    }
}
