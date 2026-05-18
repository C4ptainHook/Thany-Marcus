using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Pgvector;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class CompositeNoteSchemaV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notes_client_note_id",
                table: "notes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs");

            migrationBuilder.DropColumn(
                name: "suggested_project",
                table: "notes");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<Instant>(
                name: "deleted_at",
                table: "notes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Vector>(
                name: "embedding",
                table: "notes",
                type: "vector(256)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "hub_entity_id",
                table: "notes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_hub",
                table: "notes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "notes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "transition_version",
                table: "notes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "events_log",
                table: "ingest_jobs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "ingest_jobs",
                type: "text",
                nullable: false,
                defaultValue: "capture");

            migrationBuilder.AddColumn<long>(
                name: "transition_version",
                table: "ingest_jobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "extraction_cache_key",
                table: "attachments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_attachment_id",
                table: "attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "url",
                table: "attachments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "entities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    canonical_name = table.Column<string>(type: "text", nullable: false),
                    aliases = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    description = table.Column<string>(type: "text", nullable: true),
                    embedding = table.Column<Vector>(type: "vector(256)", nullable: true),
                    hub_note_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mention_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    source = table.Column<string>(type: "text", nullable: false),
                    is_provisional = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    vault_folder = table.Column<string>(type: "text", nullable: true),
                    deleted_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entities", x => x.id);
                    table.CheckConstraint("ck_entities_kind", "kind IN ('person','organization','project','place','concept','other')");
                    table.CheckConstraint("ck_entities_source", "source IN ('user','llm')");
                });

            migrationBuilder.CreateTable(
                name: "extraction_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ingest_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attachment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_sidecar = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    lease_expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    scheduled_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    events_log = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    transition_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extraction_tasks", x => x.id);
                    table.CheckConstraint("ck_extraction_tasks_status", "status IN ('queued','processing','succeeded','failed','skipped')");
                    table.CheckConstraint("ck_extraction_tasks_target_sidecar", "target_sidecar IN ('ollama','docling','parakeet','url','video')");
                    table.ForeignKey(
                        name: "fk_extraction_tasks_attachments_attachment_id",
                        column: x => x.attachment_id,
                        principalTable: "attachments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_extraction_tasks_ingest_jobs_ingest_job_id",
                        column: x => x.ingest_job_id,
                        principalTable: "ingest_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mentions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anchor_text = table.Column<string>(type: "text", nullable: false),
                    start_offset = table.Column<int>(type: "integer", nullable: false),
                    end_offset = table.Column<int>(type: "integer", nullable: false),
                    confidence = table.Column<float>(type: "real", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mentions", x => x.id);
                    table.ForeignKey(
                        name: "fk_mentions_entities_entity_id",
                        column: x => x.entity_id,
                        principalTable: "entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_mentions_notes_note_id",
                        column: x => x.note_id,
                        principalTable: "notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notes_client_note_id",
                table: "notes",
                column: "client_note_id",
                unique: true,
                filter: "client_note_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notes_embedding",
                table: "notes",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_notes_hub_entity",
                table: "notes",
                column: "hub_entity_id",
                filter: "is_hub");

            migrationBuilder.CreateIndex(
                name: "ix_notes_project",
                table: "notes",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_notes_updated_at",
                table: "notes",
                column: "updated_at",
                filter: "deleted_at IS NULL OR status = 'ready'");

            migrationBuilder.CreateIndex(
                name: "ix_ingest_jobs_active_per_note",
                table: "ingest_jobs",
                column: "note_id",
                unique: true,
                filter: "status NOT IN ('succeeded','failed_extraction','failed_composition','failed_route','failed_entities','failed_embedding','dead_lettered')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ingest_jobs_kind",
                table: "ingest_jobs",
                sql: "kind IN ('capture','hub_regen','reprocess')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs",
                sql: "status IN ('processing','queued','extracting_attachments','composing','routing','extracting_entities','embedding','succeeded','failed_extraction','failed_composition','failed_route','failed_entities','failed_embedding','dead_lettered')");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_cache",
                table: "attachments",
                columns: new[] { "sha256", "extraction_cache_key" },
                filter: "extracted_text IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_parent",
                table: "attachments",
                column: "parent_attachment_id");

            migrationBuilder.CreateIndex(
                name: "ix_entities_embedding",
                table: "entities",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_entities_kind_canonical_name",
                table: "entities",
                columns: new[] { "kind", "canonical_name" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_entities_kind_source",
                table: "entities",
                columns: new[] { "kind", "source" });

            migrationBuilder.CreateIndex(
                name: "ix_extraction_tasks_attachment_id",
                table: "extraction_tasks",
                column: "attachment_id");

            migrationBuilder.CreateIndex(
                name: "ix_extraction_tasks_claim",
                table: "extraction_tasks",
                columns: new[] { "target_sidecar", "status", "scheduled_at" },
                filter: "status IN ('queued','processing')");

            migrationBuilder.CreateIndex(
                name: "ix_extraction_tasks_ingest_job_id",
                table: "extraction_tasks",
                column: "ingest_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_mentions_entity_id",
                table: "mentions",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_mentions_note_id",
                table: "mentions",
                column: "note_id");

            migrationBuilder.AddForeignKey(
                name: "fk_attachments_attachments_parent_attachment_id",
                table: "attachments",
                column: "parent_attachment_id",
                principalTable: "attachments",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_notes_entities_hub_entity_id",
                table: "notes",
                column: "hub_entity_id",
                principalTable: "entities",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_notes_entities_project_id",
                table: "notes",
                column: "project_id",
                principalTable: "entities",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_attachments_attachments_parent_attachment_id",
                table: "attachments");

            migrationBuilder.DropForeignKey(
                name: "fk_notes_entities_hub_entity_id",
                table: "notes");

            migrationBuilder.DropForeignKey(
                name: "fk_notes_entities_project_id",
                table: "notes");

            migrationBuilder.DropTable(
                name: "extraction_tasks");

            migrationBuilder.DropTable(
                name: "mentions");

            migrationBuilder.DropTable(
                name: "entities");

            migrationBuilder.DropIndex(
                name: "ix_notes_client_note_id",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_notes_embedding",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_notes_hub_entity",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_notes_project",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_notes_updated_at",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_ingest_jobs_active_per_note",
                table: "ingest_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ingest_jobs_kind",
                table: "ingest_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs");

            migrationBuilder.DropIndex(
                name: "ix_attachments_cache",
                table: "attachments");

            migrationBuilder.DropIndex(
                name: "ix_attachments_parent",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "embedding",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "hub_entity_id",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "is_hub",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "transition_version",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "events_log",
                table: "ingest_jobs");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "ingest_jobs");

            migrationBuilder.DropColumn(
                name: "transition_version",
                table: "ingest_jobs");

            migrationBuilder.DropColumn(
                name: "extraction_cache_key",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "parent_attachment_id",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "url",
                table: "attachments");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<string>(
                name: "suggested_project",
                table: "notes",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_notes_client_note_id",
                table: "notes",
                column: "client_note_id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs",
                sql: "status IN ('queued','processing','succeeded','dead_lettered')");
        }
    }
}
