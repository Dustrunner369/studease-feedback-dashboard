using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SolutionIdeas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Old free-text results can't become Solution Ideas, so they're cleared; Problems show the button again.
            migrationBuilder.Sql("DELETE FROM feedback.solution_suggestions;");

            migrationBuilder.DropColumn(
                name: "body",
                schema: "feedback",
                table: "solution_suggestions");

            migrationBuilder.AddColumn<string>(
                name: "ideas",
                schema: "feedback",
                table: "solution_suggestions",
                type: "jsonb",
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM feedback.solution_suggestions;");

            migrationBuilder.DropColumn(
                name: "ideas",
                schema: "feedback",
                table: "solution_suggestions");

            migrationBuilder.AddColumn<string>(
                name: "body",
                schema: "feedback",
                table: "solution_suggestions",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
