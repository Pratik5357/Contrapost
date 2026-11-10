using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contrapost.Infrastructure.Persistence.Migrations
{
    /// <summary>Applies db/migrations/005_retention_and_app_role.sql.</summary>
    public partial class RetentionAndAppRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Read("005_retention_and_app_role.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The role is cluster-wide and may serve other databases, so only this database's grants go.
            migrationBuilder.Sql("""
                ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM contrapost_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM contrapost_app;
                REVOKE ALL ON ALL TABLES IN SCHEMA public FROM contrapost_app;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM contrapost_app;
                REVOKE USAGE ON SCHEMA public FROM contrapost_app;
                DROP INDEX ix_outbox_published;
                """);
        }
    }
}
