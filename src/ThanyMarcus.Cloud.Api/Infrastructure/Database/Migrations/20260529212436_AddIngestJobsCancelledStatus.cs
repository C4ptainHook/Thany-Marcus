using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestJobsCancelledStatus : Migration
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

            migrationBuilder.CreateIndex(
                name: "ix_ingest_jobs_active_per_note",
                table: "ingest_jobs",
                column: "note_id",
                unique: true,
                filter: "status NOT IN ('succeeded','failed_extraction','failed_composition','failed_entities','failed_route','failed_synthesis','failed_embedding','dead_lettered','cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs",
                sql: "status IN ('queued','extracting_attachments','composing','extracting_entities','routing','synthesizing','embedding','succeeded','failed_extraction','failed_composition','failed_entities','failed_route','failed_synthesis','failed_embedding','dead_lettered','cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ingest_jobs_active_per_note",
                table: "ingest_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs");

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
    }
}
