using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyProcessingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE ingest_jobs SET status = 'extracting_attachments' WHERE status = 'processing';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs",
                sql: "status IN ('queued','extracting_attachments','composing','routing','extracting_entities','embedding','succeeded','failed_extraction','failed_composition','failed_route','failed_entities','failed_embedding','dead_lettered')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ingest_jobs_status",
                table: "ingest_jobs",
                sql: "status IN ('processing','queued','extracting_attachments','composing','routing','extracting_entities','embedding','succeeded','failed_extraction','failed_composition','failed_route','failed_entities','failed_embedding','dead_lettered')");
        }
    }
}
