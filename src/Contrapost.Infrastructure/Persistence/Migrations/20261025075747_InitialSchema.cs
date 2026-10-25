using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contrapost.Infrastructure.Persistence.Migrations
{
    /// <summary>Creates the full ledger schema and its guards from db/schema.sql.</summary>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Read("schema.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The ledger is append-only by design; there is no supported way to roll back
            // the schema that holds it. Restore from a backup instead.
            throw new NotSupportedException("The initial ledger schema cannot be rolled back.");
        }
    }
}
