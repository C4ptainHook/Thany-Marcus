using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class ProvisioningSagaFkRestrict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_provisioning_jobs_clouds_cloud_id",
                table: "provisioning_jobs");

            migrationBuilder.DropIndex(
                name: "ix_provisioning_jobs_pending_created_at",
                table: "provisioning_jobs");

            migrationBuilder.AddForeignKey(
                name: "fk_provisioning_jobs_clouds_cloud_id",
                table: "provisioning_jobs",
                column: "cloud_id",
                principalTable: "clouds",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_provisioning_jobs_clouds_cloud_id",
                table: "provisioning_jobs");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_jobs_pending_created_at",
                table: "provisioning_jobs",
                column: "created_at",
                filter: "status = 'pending'");

            migrationBuilder.AddForeignKey(
                name: "fk_provisioning_jobs_clouds_cloud_id",
                table: "provisioning_jobs",
                column: "cloud_id",
                principalTable: "clouds",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
