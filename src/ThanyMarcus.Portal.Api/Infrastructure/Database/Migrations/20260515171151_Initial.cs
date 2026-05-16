using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    google_subject = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    profile_picture_url = table.Column<string>(type: "text", nullable: true),
                    sessions_invalidated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    passphrase_argon2salt = table.Column<byte[]>(type: "bytea", nullable: true),
                    passphrase_argon2params = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    passphrase_wrapped_dek = table.Column<byte[]>(type: "bytea", nullable: true),
                    passphrase_wrap_nonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    passphrase_wrap_tag = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "auth_lockouts",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    failed_count = table.Column<short>(type: "smallint", nullable: false),
                    locked_until = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_lockouts", x => new { x.user_id, x.kind });
                    table.ForeignKey(
                        name: "fk_auth_lockouts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clouds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    hostname = table.Column<string>(type: "text", nullable: false),
                    provisioning_status = table.Column<string>(type: "text", nullable: false),
                    plan_started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    apply_started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    dns_started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    cert_started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    admin_started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    provisioning_completed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    provisioning_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    destroyed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clouds", x => x.id);
                    table.ForeignKey(
                        name: "fk_clouds_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "encrypted_provider_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_encrypted_provider_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_encrypted_provider_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recovery_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hashed_code = table.Column<string>(type: "text", nullable: false),
                    wrap_argon2salt = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrap_argon2params = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    wrapped_dek = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrap_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrap_tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    used_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_recovery_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "totp_backup_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hashed_code = table.Column<string>(type: "text", nullable: false),
                    used_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_totp_backup_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_totp_backup_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "totp_secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    enabled_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    disabled_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_totp_secrets", x => x.id);
                    table.ForeignKey(
                        name: "fk_totp_secrets_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plugin_token_metadata",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cloud_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    last_used_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plugin_token_metadata", x => x.id);
                    table.ForeignKey(
                        name: "fk_plugin_token_metadata_clouds_cloud_id",
                        column: x => x.cloud_id,
                        principalTable: "clouds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provisioning_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cloud_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    worker_id = table.Column<string>(type: "text", nullable: true),
                    lease_expires = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provisioning_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_provisioning_jobs_clouds_cloud_id",
                        column: x => x.cloud_id,
                        principalTable: "clouds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clouds_hostname",
                table: "clouds",
                column: "hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clouds_user_id",
                table: "clouds",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_encrypted_provider_tokens_user_id_provider",
                table: "encrypted_provider_tokens",
                columns: new[] { "user_id", "provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plugin_token_metadata_cloud_id",
                table: "plugin_token_metadata",
                column: "cloud_id");

            migrationBuilder.CreateIndex(
                name: "ix_plugin_token_metadata_token_hash",
                table: "plugin_token_metadata",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_jobs_cloud_id",
                table: "provisioning_jobs",
                column: "cloud_id");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_jobs_inprogress_lease",
                table: "provisioning_jobs",
                column: "lease_expires",
                filter: "status = 'in_progress'");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_jobs_pending_created_at",
                table: "provisioning_jobs",
                column: "created_at",
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_recovery_codes_user_id",
                table: "recovery_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_totp_backup_codes_user_id",
                table: "totp_backup_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_totp_secrets_user_id",
                table: "totp_secrets",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_google_subject",
                table: "users",
                column: "google_subject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_lockouts");

            migrationBuilder.DropTable(
                name: "encrypted_provider_tokens");

            migrationBuilder.DropTable(
                name: "plugin_token_metadata");

            migrationBuilder.DropTable(
                name: "provisioning_jobs");

            migrationBuilder.DropTable(
                name: "recovery_codes");

            migrationBuilder.DropTable(
                name: "totp_backup_codes");

            migrationBuilder.DropTable(
                name: "totp_secrets");

            migrationBuilder.DropTable(
                name: "clouds");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
