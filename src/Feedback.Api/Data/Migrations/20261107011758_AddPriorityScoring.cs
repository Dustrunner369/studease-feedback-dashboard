using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Feedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPriorityScoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "current_scoring_id",
                schema: "feedback",
                table: "problems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "first_scoring_id",
                schema: "feedback",
                table: "problems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reach_points",
                schema: "feedback",
                table: "problems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reach_rating",
                schema: "feedback",
                table: "problems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recommended_level",
                schema: "feedback",
                table: "problems",
                type: "character varying(6)",
                maxLength: 6,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "recommended_score",
                schema: "feedback",
                table: "problems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "scoring_failed",
                schema: "feedback",
                table: "problems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "priority_scorings",
                schema: "feedback",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    problem_id = table.Column<long>(type: "bigint", nullable: false),
                    strategic_fit = table.Column<int>(type: "integer", nullable: false),
                    strategic_fit_reason = table.Column<string>(type: "text", nullable: false),
                    strategic_fit_goal = table.Column<string>(type: "text", nullable: true),
                    pain = table.Column<int>(type: "integer", nullable: false),
                    pain_reason = table.Column<string>(type: "text", nullable: false),
                    retention_risk = table.Column<int>(type: "integer", nullable: false),
                    retention_risk_reason = table.Column<string>(type: "text", nullable: false),
                    ai_call_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_priority_scorings", x => x.id);
                    table.CheckConstraint("ck_priority_scorings_pain", "pain BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_priority_scorings_retention_risk", "retention_risk BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_priority_scorings_strategic_fit", "strategic_fit BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_priority_scorings_problems_problem_id",
                        column: x => x.problem_id,
                        principalSchema: "feedback",
                        principalTable: "problems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_problems_current_scoring_id",
                schema: "feedback",
                table: "problems",
                column: "current_scoring_id");

            migrationBuilder.CreateIndex(
                name: "ix_problems_first_scoring_id",
                schema: "feedback",
                table: "problems",
                column: "first_scoring_id");

            migrationBuilder.CreateIndex(
                name: "ix_problems_recommended_score",
                schema: "feedback",
                table: "problems",
                column: "recommended_score");

            migrationBuilder.CreateIndex(
                name: "ix_priority_scorings_problem_id",
                schema: "feedback",
                table: "priority_scorings",
                column: "problem_id");

            migrationBuilder.AddForeignKey(
                name: "fk_problems_priority_scorings_current_scoring_id",
                schema: "feedback",
                table: "problems",
                column: "current_scoring_id",
                principalSchema: "feedback",
                principalTable: "priority_scorings",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_problems_priority_scorings_first_scoring_id",
                schema: "feedback",
                table: "problems",
                column: "first_scoring_id",
                principalSchema: "feedback",
                principalTable: "priority_scorings",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_problems_priority_scorings_current_scoring_id",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropForeignKey(
                name: "fk_problems_priority_scorings_first_scoring_id",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropTable(
                name: "priority_scorings",
                schema: "feedback");

            migrationBuilder.DropIndex(
                name: "ix_problems_current_scoring_id",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropIndex(
                name: "ix_problems_first_scoring_id",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropIndex(
                name: "ix_problems_recommended_score",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "current_scoring_id",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "first_scoring_id",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "reach_points",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "reach_rating",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "recommended_level",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "recommended_score",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "scoring_failed",
                schema: "feedback",
                table: "problems");
        }
    }
}
