using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachmentExtractedMinimalStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_extraction_status",
                table: "attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_extraction_status",
                table: "attachments",
                sql: "extraction_status IN ('pending','extracted','extracted_minimal','skipped','failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_extraction_status",
                table: "attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_extraction_status",
                table: "attachments",
                sql: "extraction_status IN ('pending','extracted','skipped','failed')");
        }
    }
}
