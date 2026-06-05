using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class ReembedVaultClsPooling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE notes SET embedding = NULL WHERE embedding IS NOT NULL;");

            migrationBuilder.Sql(
                """
                INSERT INTO ingest_jobs
                    (id, note_id, kind, status, attempts, consecutive_crashes,
                     events_log, transition_version, scheduled_at, created_at, updated_at)
                SELECT gen_random_uuid(), n.id, 'user_edit_embed', 'embedding', 0, 0,
                       '[]'::jsonb, 0, now(), now(), now()
                  FROM notes n
                 WHERE n.deleted_at IS NULL
                   AND n.status = 'ready'
                   AND NOT EXISTS (
                       SELECT 1 FROM ingest_jobs j
                        WHERE j.note_id = n.id
                          AND j.status NOT IN (
                              'succeeded','failed_extraction','failed_composition',
                              'failed_entities','failed_route','failed_synthesis',
                              'failed_embedding','dead_lettered','cancelled'));
                """);

            migrationBuilder.Sql("NOTIFY ingest_jobs_new;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
