using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class DropNotesProjectId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_notes_entities_project_id",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_notes_project",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "notes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "notes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_notes_project",
                table: "notes",
                column: "project_id");

            migrationBuilder.AddForeignKey(
                name: "fk_notes_entities_project_id",
                table: "notes",
                column: "project_id",
                principalTable: "entities",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
