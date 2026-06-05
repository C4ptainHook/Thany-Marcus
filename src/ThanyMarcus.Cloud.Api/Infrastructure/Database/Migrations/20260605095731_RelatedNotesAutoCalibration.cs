using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class RelatedNotesAutoCalibration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "related_notes_auto_entity_count",
                table: "cloud_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "related_notes_auto_note_count",
                table: "cloud_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "related_notes_max_distance_auto",
                table: "cloud_settings",
                type: "double precision",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "cloud_settings",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "related_notes_auto_entity_count", "related_notes_auto_note_count", "related_notes_max_distance_auto" },
                values: new object[] { null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "related_notes_auto_entity_count",
                table: "cloud_settings");

            migrationBuilder.DropColumn(
                name: "related_notes_auto_note_count",
                table: "cloud_settings");

            migrationBuilder.DropColumn(
                name: "related_notes_max_distance_auto",
                table: "cloud_settings");
        }
    }
}
