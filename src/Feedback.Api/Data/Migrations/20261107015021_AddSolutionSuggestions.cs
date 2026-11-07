using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSolutionSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "solution_suggestions",
                schema: "feedback",
                columns: table => new
                {
                    problem_id = table.Column<long>(type: "bigint", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    files = table.Column<string[]>(type: "text[]", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    picker_ai_call_id = table.Column<long>(type: "bigint", nullable: false),
                    write_ai_call_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_solution_suggestions", x => x.problem_id);
                    table.ForeignKey(
                        name: "fk_solution_suggestions_problems_problem_id",
                        column: x => x.problem_id,
                        principalSchema: "feedback",
                        principalTable: "problems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "solution_suggestions",
                schema: "feedback");
        }
    }
}
