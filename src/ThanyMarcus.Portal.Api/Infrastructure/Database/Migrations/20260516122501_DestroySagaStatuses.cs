using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class DestroySagaStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_provisioning_jobs_active_next_visible_at",
                table: "provisioning_jobs");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_jobs_active_next_visible_at",
                table: "provisioning_jobs",
                column: "next_visible_at",
                filter: "status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','failed_destroy','cancelled','rolled_back')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_provisioning_jobs_active_next_visible_at",
                table: "provisioning_jobs");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_jobs_active_next_visible_at",
                table: "provisioning_jobs",
                column: "next_visible_at",
                filter: "status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','cancelled','rolled_back')");
        }
    }
}
