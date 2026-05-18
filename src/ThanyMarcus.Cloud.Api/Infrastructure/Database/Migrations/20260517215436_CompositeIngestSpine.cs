using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class CompositeIngestSpine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cloud_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    llm_mode = table.Column<string>(type: "text", nullable: false, defaultValue: "safe"),
                    encrypted_external_api_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    llm_model = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cloud_settings", x => x.id);
                    table.CheckConstraint("ck_cloud_settings_llm_mode", "llm_mode IN ('safe','unsafe_anthropic','unsafe_openai')");
                    table.CheckConstraint("ck_cloud_settings_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_note_id = table.Column<string>(type: "text", nullable: true),
                    captured_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    body_input = table.Column<string>(type: "text", nullable: false),
                    relative_path = table.Column<string>(type: "text", nullable: true),
                    body_output = table.Column<string>(type: "text", nullable: true),
                    suggested_project = table.Column<string>(type: "text", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: true),
                    llm_mode = table.Column<string>(type: "text", nullable: true),
                    provenance = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notes", x => x.id);
                    table.CheckConstraint("ck_notes_llm_mode", "llm_mode IS NULL OR llm_mode IN ('safe','unsafe_anthropic','unsafe_openai')");
                    table.CheckConstraint("ck_notes_status", "status IN ('pending','processing','ready','failed')");
                });

            migrationBuilder.CreateTable(
                name: "plugin_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plugin_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_attachment_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    storage_provider = table.Column<string>(type: "text", nullable: false),
                    storage_bucket = table.Column<string>(type: "text", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: true),
                    mime_type = table.Column<string>(type: "text", nullable: true),
                    sha256 = table.Column<string>(type: "text", nullable: true),
                    filename = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    extraction_status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    extracted_text = table.Column<string>(type: "text", nullable: true),
                    extraction_error = table.Column<string>(type: "text", nullable: true),
                    extra = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachments", x => x.id);
                    table.CheckConstraint("ck_attachments_extraction_status", "extraction_status IN ('pending','extracted','skipped','failed')");
                    table.CheckConstraint("ck_attachments_kind", "kind IN ('url','image','voice','file')");
                    table.CheckConstraint("ck_attachments_status", "status IN ('pending','awaiting_upload','uploaded')");
                    table.ForeignKey(
                        name: "fk_attachments_notes_note_id",
                        column: x => x.note_id,
                        principalTable: "notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ingest_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<short>(type: "smallint", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    lease_expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    scheduled_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ingest_jobs", x => x.id);
                    table.CheckConstraint("ck_ingest_jobs_status", "status IN ('queued','processing','succeeded','dead_lettered')");
                    table.ForeignKey(
                        name: "fk_ingest_jobs_notes_note_id",
                        column: x => x.note_id,
                        principalTable: "notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "cloud_settings",
                columns: new[] { "id", "encrypted_external_api_key", "llm_mode", "llm_model", "updated_at" },
                values: new object[] { 1, null, "safe", null, NodaTime.Instant.FromUnixTimeTicks(17790624000000000L) });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_note_id",
                table: "attachments",
                column: "note_id");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_note_id_client_attachment_id",
                table: "attachments",
                columns: new[] { "note_id", "client_attachment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attachments_storage_key",
                table: "attachments",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ingest_jobs_note_id",
                table: "ingest_jobs",
                column: "note_id");

            migrationBuilder.CreateIndex(
                name: "ix_ingest_jobs_queued_scheduled_at",
                table: "ingest_jobs",
                columns: new[] { "status", "scheduled_at" },
                filter: "status = 'queued'");

            migrationBuilder.CreateIndex(
                name: "ix_notes_client_note_id",
                table: "notes",
                column: "client_note_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notes_status_updated_at",
                table: "notes",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_plugin_tokens_token_hash",
                table: "plugin_tokens",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.DropTable(
                name: "cloud_settings");

            migrationBuilder.DropTable(
                name: "ingest_jobs");

            migrationBuilder.DropTable(
                name: "plugin_tokens");

            migrationBuilder.DropTable(
                name: "notes");
        }
    }
}
