using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddUsernameAndNullableSso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "google_subject",
                table: "users",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "email",
                table: "users",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "username",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill: derive the username from the email local-part for existing Google-SSO
            // users, stripping anything outside [a-z0-9_-]. Empty/too-short results fall back to
            // a generic handle, then case-insensitive collisions get a numeric suffix.
            migrationBuilder.Sql(
                "UPDATE users " +
                "SET username = regexp_replace(lower(split_part(email, '@', 1)), '[^a-z0-9_-]', '', 'g') " +
                "WHERE username = '' AND email IS NOT NULL;");
            migrationBuilder.Sql("UPDATE users SET username = 'user' WHERE length(username) < 3;");
            migrationBuilder.Sql(
                "WITH ranked AS (" +
                "  SELECT id, row_number() OVER (PARTITION BY lower(username) ORDER BY created_at, id) AS rn FROM users" +
                ") " +
                "UPDATE users u SET username = u.username || (r.rn - 1)::text " +
                "FROM ranked r WHERE u.id = r.id AND r.rn > 1;");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_users_username_lower ON users (lower(username));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_users_username_lower;");

            migrationBuilder.DropColumn(
                name: "username",
                table: "users");

            migrationBuilder.AlterColumn<string>(
                name: "google_subject",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "email",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
