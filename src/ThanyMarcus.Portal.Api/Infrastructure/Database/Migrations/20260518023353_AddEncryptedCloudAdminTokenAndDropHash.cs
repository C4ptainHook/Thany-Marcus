using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddEncryptedCloudAdminTokenAndDropHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Cloud admin token: drop the SHA-256 hash column (unusable for outgoing /admin/*
            // calls) and add a nullable encrypted_cloud_admin_token bytea column. Previously
            // provisioned clouds therefore land with NULL and must be re-provisioned to gain
            // a working portal→cloud admin path. See ADR-0041.
            migrationBuilder.DropColumn(
                name: "cloud_admin_token_hash",
                table: "clouds");

            migrationBuilder.AddColumn<byte[]>(
                name: "encrypted_cloud_admin_token",
                table: "clouds",
                type: "bytea",
                nullable: true);

            // plugin_token_metadata.token_hash: hex-string -> raw 32 bytes (matches cloud-side
            // plugin_tokens.token_hash). No backfill: pre-existing rows are not load-bearing
            // (no working plugin yet).
            migrationBuilder.DropIndex(
                name: "ix_plugin_token_metadata_token_hash",
                table: "plugin_token_metadata");

            migrationBuilder.DropColumn(
                name: "token_hash",
                table: "plugin_token_metadata");

            migrationBuilder.AddColumn<byte[]>(
                name: "token_hash",
                table: "plugin_token_metadata",
                type: "bytea",
                nullable: false,
                defaultValue: Array.Empty<byte>());

            migrationBuilder.CreateIndex(
                name: "ix_plugin_token_metadata_token_hash",
                table: "plugin_token_metadata",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_plugin_token_metadata_token_hash",
                table: "plugin_token_metadata");

            migrationBuilder.DropColumn(
                name: "token_hash",
                table: "plugin_token_metadata");

            migrationBuilder.AddColumn<string>(
                name: "token_hash",
                table: "plugin_token_metadata",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_plugin_token_metadata_token_hash",
                table: "plugin_token_metadata",
                column: "token_hash",
                unique: true);

            migrationBuilder.DropColumn(
                name: "encrypted_cloud_admin_token",
                table: "clouds");

            migrationBuilder.AddColumn<byte[]>(
                name: "cloud_admin_token_hash",
                table: "clouds",
                type: "bytea",
                nullable: true);
        }
    }
}
