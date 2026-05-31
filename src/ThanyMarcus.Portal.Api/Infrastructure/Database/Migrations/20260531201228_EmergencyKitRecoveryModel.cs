using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class EmergencyKitRecoveryModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "totp_wrapped_dek",
                table: "users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "totp_wrap_nonce",
                table: "users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "totp_wrap_tag",
                table: "users",
                type: "bytea",
                nullable: true);

            // Repurpose recovery_codes in place: the envelope columns are identical, so the
            // single-kit-per-user model is a rename rather than a destructive drop/create.
            migrationBuilder.RenameTable(name: "recovery_codes", newName: "emergency_kits");
            migrationBuilder.RenameColumn(name: "hashed_code", table: "emergency_kits", newName: "hashed_string");

            migrationBuilder.AddColumn<Instant>(
                name: "revoked_at",
                table: "emergency_kits",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("ALTER TABLE emergency_kits RENAME CONSTRAINT pk_recovery_codes TO pk_emergency_kits;");
            migrationBuilder.Sql("ALTER TABLE emergency_kits RENAME CONSTRAINT fk_recovery_codes_users_user_id TO fk_emergency_kits_users_user_id;");
            migrationBuilder.Sql("ALTER INDEX ix_recovery_codes_user_id RENAME TO ix_emergency_kits_user_id;");

            // Any rows from the old multi-code regime are not valid single kits; invalidate them
            // so users are re-issued one Emergency Kit on their next passphrase set/change.
            migrationBuilder.Sql("UPDATE emergency_kits SET revoked_at = NOW() WHERE revoked_at IS NULL;");

            migrationBuilder.CreateIndex(
                name: "ix_emergency_kits_active_per_user",
                table: "emergency_kits",
                column: "user_id",
                unique: true,
                filter: "used_at IS NULL AND revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_emergency_kits_active_per_user",
                table: "emergency_kits");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "emergency_kits");

            migrationBuilder.Sql("ALTER INDEX ix_emergency_kits_user_id RENAME TO ix_recovery_codes_user_id;");
            migrationBuilder.Sql("ALTER TABLE emergency_kits RENAME CONSTRAINT fk_emergency_kits_users_user_id TO fk_recovery_codes_users_user_id;");
            migrationBuilder.Sql("ALTER TABLE emergency_kits RENAME CONSTRAINT pk_emergency_kits TO pk_recovery_codes;");

            migrationBuilder.RenameColumn(name: "hashed_string", table: "emergency_kits", newName: "hashed_code");
            migrationBuilder.RenameTable(name: "emergency_kits", newName: "recovery_codes");

            migrationBuilder.DropColumn(name: "totp_wrap_tag", table: "users");
            migrationBuilder.DropColumn(name: "totp_wrap_nonce", table: "users");
            migrationBuilder.DropColumn(name: "totp_wrapped_dek", table: "users");
        }
    }
}
