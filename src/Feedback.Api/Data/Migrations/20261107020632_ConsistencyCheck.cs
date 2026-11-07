using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Feedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConsistencyCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consistency_runs",
                schema: "feedback",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ran_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consistency_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "consistency_results",
                schema: "feedback",
                columns: table => new
                {
                    run_id = table.Column<long>(type: "bigint", nullable: false),
                    problem_id = table.Column<long>(type: "bigint", nullable: false),
                    levels = table.Column<string[]>(type: "text[]", nullable: false),
                    scores = table.Column<int[]>(type: "integer[]", nullable: false),
                    couldnt_check = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consistency_results", x => new { x.run_id, x.problem_id });
                    table.ForeignKey(
                        name: "fk_consistency_results_consistency_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "feedback",
                        principalTable: "consistency_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_consistency_results_problems_problem_id",
                        column: x => x.problem_id,
                        principalSchema: "feedback",
                        principalTable: "problems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_consistency_results_problem_id",
                schema: "feedback",
                table: "consistency_results",
                column: "problem_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consistency_results",
                schema: "feedback");

            migrationBuilder.DropTable(
                name: "consistency_runs",
                schema: "feedback");
        }
    }
}
