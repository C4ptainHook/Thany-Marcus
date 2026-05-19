using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddUserEditEmbedJobKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ingest_jobs_kind",
                table: "ingest_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ingest_jobs_kind",
                table: "ingest_jobs",
                sql: "kind IN ('capture','hub_regen','reprocess','user_edit_embed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ingest_jobs_kind",
                table: "ingest_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ingest_jobs_kind",
                table: "ingest_jobs",
                sql: "kind IN ('capture','hub_regen','reprocess')");
        }
    }
}
