using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachmentMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_extraction_status",
                table: "attachments");

            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "attachments",
                type: "text",
                nullable: false,
                defaultValue: "extract");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_extraction_status",
                table: "attachments",
                sql: "extraction_status IN ('pending','extracted','extracted_minimal','skipped','referenced','failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_metadata_only_url",
                table: "attachments",
                sql: "mode <> 'metadata' OR kind = 'url'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_mode",
                table: "attachments",
                sql: "mode IN ('extract','reference','metadata')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_extraction_status",
                table: "attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_metadata_only_url",
                table: "attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_mode",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_extraction_status",
                table: "attachments",
                sql: "extraction_status IN ('pending','extracted','extracted_minimal','skipped','failed')");
        }
    }
}
