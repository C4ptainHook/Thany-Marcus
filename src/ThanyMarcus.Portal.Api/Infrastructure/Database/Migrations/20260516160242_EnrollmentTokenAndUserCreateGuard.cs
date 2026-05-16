using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class EnrollmentTokenAndUserCreateGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "enrollment_token",
                table: "provisioning_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "provisioning_jobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill denormalized user_id from clouds (FK chain via cloud_id).
            migrationBuilder.Sql(
                "UPDATE provisioning_jobs pj SET user_id = c.user_id " +
                "FROM clouds c WHERE pj.cloud_id = c.id;");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_jobs_user_create_active",
                table: "provisioning_jobs",
                column: "user_id",
                filter: "kind = 'create' AND status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','failed_destroy','cancelled','rolled_back')");

            migrationBuilder.AddForeignKey(
                name: "fk_provisioning_jobs_users_user_id",
                table: "provisioning_jobs",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_provisioning_jobs_users_user_id",
                table: "provisioning_jobs");

            migrationBuilder.DropIndex(
                name: "ix_provisioning_jobs_user_create_active",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "enrollment_token",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "provisioning_jobs");
        }
    }
}
