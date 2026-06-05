using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class CrossLanguageUnification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "suggested_merge_distance",
                table: "entity_suggestions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "suggested_merge_entity_id",
                table: "entity_suggestions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "entities",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_entity_suggestions_suggested_merge_entity_id",
                table: "entity_suggestions",
                column: "suggested_merge_entity_id");

            migrationBuilder.AddForeignKey(
                name: "fk_entity_suggestions_entities_suggested_merge_entity_id",
                table: "entity_suggestions",
                column: "suggested_merge_entity_id",
                principalTable: "entities",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_entity_suggestions_entities_suggested_merge_entity_id",
                table: "entity_suggestions");

            migrationBuilder.DropIndex(
                name: "ix_entity_suggestions_suggested_merge_entity_id",
                table: "entity_suggestions");

            migrationBuilder.DropColumn(
                name: "suggested_merge_distance",
                table: "entity_suggestions");

            migrationBuilder.DropColumn(
                name: "suggested_merge_entity_id",
                table: "entity_suggestions");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "entities");
        }
    }
}
