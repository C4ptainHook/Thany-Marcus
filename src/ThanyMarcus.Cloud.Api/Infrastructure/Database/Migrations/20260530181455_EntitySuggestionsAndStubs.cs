using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Pgvector;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class EntitySuggestionsAndStubs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Auto-creation of provisional entities is removed; only user-confirmed rows exist now.
            migrationBuilder.Sql("DELETE FROM entities WHERE is_provisional = TRUE;");

            migrationBuilder.DropColumn(
                name: "is_provisional",
                table: "entities");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "notes",
                type: "text",
                nullable: false,
                defaultValue: "synth_note");

            migrationBuilder.AddColumn<Guid>(
                name: "stub_note_id",
                table: "entities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "entity_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_text = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    aliases = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    occurrences = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    occurrence_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    distinct_note_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    embedding = table.Column<Vector>(type: "vector(256)", nullable: true),
                    first_seen_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    accepted_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dismissed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entity_suggestions", x => x.id);
                    table.CheckConstraint("ck_entity_suggestions_kind", "kind IN ('person','organization','project','place','concept','other')");
                    table.ForeignKey(
                        name: "fk_entity_suggestions_entities_accepted_entity_id",
                        column: x => x.accepted_entity_id,
                        principalTable: "entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notes_kind",
                table: "notes",
                column: "kind",
                filter: "deleted_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notes_kind",
                table: "notes",
                sql: "kind IN ('synth_note','entity_stub')");

            migrationBuilder.CreateIndex(
                name: "ix_entities_stub_note",
                table: "entities",
                column: "stub_note_id",
                unique: true,
                filter: "stub_note_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_entity_suggestions_accepted_entity_id",
                table: "entity_suggestions",
                column: "accepted_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_entity_suggestions_surfaceable",
                table: "entity_suggestions",
                columns: new[] { "occurrence_count", "last_seen_at" },
                descending: new[] { true, true },
                filter: "accepted_at IS NULL AND dismissed_at IS NULL");

            // Functional partial unique index — same surface form (case-insensitive) + kind aggregates
            // into one open suggestion. The fluent API can't express LOWER(...), so it lives in raw SQL.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ix_entity_suggestions_canonical_kind
                    ON entity_suggestions (LOWER(canonical_text), kind)
                 WHERE accepted_at IS NULL AND dismissed_at IS NULL;
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_entities_notes_stub_note_id",
                table: "entities",
                column: "stub_note_id",
                principalTable: "notes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_entities_notes_stub_note_id",
                table: "entities");

            migrationBuilder.DropTable(
                name: "entity_suggestions");

            migrationBuilder.DropIndex(
                name: "ix_notes_kind",
                table: "notes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notes_kind",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_entities_stub_note",
                table: "entities");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "stub_note_id",
                table: "entities");

            migrationBuilder.AddColumn<bool>(
                name: "is_provisional",
                table: "entities",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
