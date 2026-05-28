using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class SynthesisRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ingest_jobs_active_per_note",
                table: "ingest_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs");

            migrationBuilder.AddColumn<string>(
                name: "privacy_mode",
                table: "notes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_model",
                table: "notes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "synthesis_cache_key",
                table: "notes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "synthesis_cache_value",
                table: "notes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "synthesis_preset",
                table: "notes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "synthesis_prompt_body",
                table: "notes",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_notes_synthesis_cache_key",
                table: "notes",
                column: "synthesis_cache_key",
                filter: "synthesis_cache_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ingest_jobs_active_per_note",
                table: "ingest_jobs",
                column: "note_id",
                unique: true,
                filter: "status NOT IN ('succeeded','failed_extraction','failed_composition','failed_entities','failed_route','failed_synthesis','failed_embedding','dead_lettered')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs",
                sql: "status IN ('queued','extracting_attachments','composing','extracting_entities','routing','synthesizing','embedding','succeeded','failed_extraction','failed_composition','failed_entities','failed_route','failed_synthesis','failed_embedding','dead_lettered')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notes_synthesis_cache_key",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_ingest_jobs_active_per_note",
                table: "ingest_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs");

            migrationBuilder.DropColumn(
                name: "privacy_mode",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "public_model",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "synthesis_cache_key",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "synthesis_cache_value",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "synthesis_preset",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "synthesis_prompt_body",
                table: "notes");

            migrationBuilder.CreateIndex(
                name: "ix_ingest_jobs_active_per_note",
                table: "ingest_jobs",
                column: "note_id",
                unique: true,
                filter: "status NOT IN ('succeeded','failed_extraction','failed_composition','failed_route','failed_entities','failed_embedding','dead_lettered')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs",
                sql: "status IN ('queued','extracting_attachments','composing','routing','extracting_entities','embedding','succeeded','failed_extraction','failed_composition','failed_route','failed_entities','failed_embedding','dead_lettered')");
        }
    }
}
