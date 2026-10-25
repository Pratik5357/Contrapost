using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contrapost.Infrastructure.Persistence.Migrations
{
    /// <summary>Applies db/migrations/003_reconciliation_indexes.sql.</summary>
    public partial class ReconciliationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Read("003_reconciliation_indexes.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_holds_open_debit; DROP INDEX ix_holds_open_credit; DROP INDEX ix_outbox_aggregate;");
        }
    }
}
