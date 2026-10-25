using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contrapost.Infrastructure.Persistence.Migrations
{
    /// <summary>Applies db/migrations/002_read_models.sql: tables owned by the event consumers.</summary>
    public partial class ReadModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Read("002_read_models.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Read models can be rebuilt from the ledger's postings, so dropping them loses nothing.
            migrationBuilder.Sql("DROP TABLE low_balance_alerts; DROP TABLE account_statement_heads; DROP TABLE account_statements;");
        }
    }
}
