using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class DropProjectEntityKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE entities
                SET deleted_at = NOW(), updated_at = NOW()
                WHERE kind = 'project' AND deleted_at IS NULL;
            """);

            migrationBuilder.Sql("ALTER TABLE entities DROP CONSTRAINT IF EXISTS ck_entities_kind;");
            migrationBuilder.Sql("""
                ALTER TABLE entities
                ADD CONSTRAINT ck_entities_kind
                CHECK (kind IN ('person','organization','place','concept','other')) NOT VALID;
            """);

            migrationBuilder.Sql("ALTER TABLE entity_suggestions DROP CONSTRAINT IF EXISTS ck_entity_suggestions_kind;");
            migrationBuilder.Sql("""
                ALTER TABLE entity_suggestions
                ADD CONSTRAINT ck_entity_suggestions_kind
                CHECK (kind IN ('person','organization','place','concept','other')) NOT VALID;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_entity_suggestions_kind",
                table: "entity_suggestions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_entities_kind",
                table: "entities");

            migrationBuilder.AddCheckConstraint(
                name: "ck_entity_suggestions_kind",
                table: "entity_suggestions",
                sql: "kind IN ('person','organization','project','place','concept','other')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_entities_kind",
                table: "entities",
                sql: "kind IN ('person','organization','project','place','concept','other')");
        }
    }
}
