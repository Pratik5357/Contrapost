using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contrapost.Infrastructure.Persistence.Migrations
{
    /// <summary>Applies db/migrations/004_outbox_traceparent.sql.</summary>
    public partial class OutboxTraceparent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Read("004_outbox_traceparent.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE outbox_messages DROP COLUMN traceparent;");
        }
    }
}
