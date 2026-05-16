using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class ProvisioningSagaColumnsAlignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_provisioning_jobs_inprogress_lease",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "provisioning_jobs");

            migrationBuilder.RenameColumn(
                name: "worker_id",
                table: "provisioning_jobs",
                newName: "claimed_by");

            migrationBuilder.RenameColumn(
                name: "lease_expires",
                table: "provisioning_jobs",
                newName: "lease_expires_at");

            migrationBuilder.AddColumn<short>(
                name: "attempt_count",
                table: "provisioning_jobs",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<Instant>(
                name: "phase_started_at",
                table: "provisioning_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "events_log",
                table: "provisioning_jobs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<Instant>(
                name: "next_visible_at",
                table: "provisioning_jobs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<JsonDocument>(
                name: "tf_outputs",
                table: "provisioning_jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "cloud_admin_token_hash",
                table: "clouds",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "provider_token_id",
                table: "clouds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subdomain",
                table: "clouds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "terraform_workspace",
                table: "clouds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vm_ip",
                table: "clouds",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_jobs_active_next_visible_at",
                table: "provisioning_jobs",
                column: "next_visible_at",
                filter: "status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','cancelled','rolled_back')");

            migrationBuilder.CreateIndex(
                name: "ix_clouds_provider_token_id",
                table: "clouds",
                column: "provider_token_id");

            migrationBuilder.CreateIndex(
                name: "ix_clouds_subdomain",
                table: "clouds",
                column: "subdomain",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_clouds_encrypted_provider_tokens_provider_token_id",
                table: "clouds",
                column: "provider_token_id",
                principalTable: "encrypted_provider_tokens",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_clouds_encrypted_provider_tokens_provider_token_id",
                table: "clouds");

            migrationBuilder.DropIndex(
                name: "ix_provisioning_jobs_active_next_visible_at",
                table: "provisioning_jobs");

            migrationBuilder.DropIndex(
                name: "ix_clouds_provider_token_id",
                table: "clouds");

            migrationBuilder.DropIndex(
                name: "ix_clouds_subdomain",
                table: "clouds");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "events_log",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "next_visible_at",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "phase_started_at",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "tf_outputs",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "cloud_admin_token_hash",
                table: "clouds");

            migrationBuilder.DropColumn(
                name: "provider_token_id",
                table: "clouds");

            migrationBuilder.DropColumn(
                name: "subdomain",
                table: "clouds");

            migrationBuilder.DropColumn(
                name: "terraform_workspace",
                table: "clouds");

            migrationBuilder.DropColumn(
                name: "vm_ip",
                table: "clouds");

            migrationBuilder.RenameColumn(
                name: "lease_expires_at",
                table: "provisioning_jobs",
                newName: "lease_expires");

            migrationBuilder.RenameColumn(
                name: "claimed_by",
                table: "provisioning_jobs",
                newName: "worker_id");

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "provisioning_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_jobs_inprogress_lease",
                table: "provisioning_jobs",
                column: "lease_expires",
                filter: "status = 'in_progress'");
        }
    }
}
