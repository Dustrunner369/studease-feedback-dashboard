using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Feedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPriorityAndDecisionLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "priority",
                schema: "feedback",
                table: "problems",
                type: "character varying(6)",
                maxLength: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "priority_override_reason",
                schema: "feedback",
                table: "problems",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "decision_log",
                schema: "feedback",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    problem_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_log", x => x.id);
                    table.CheckConstraint("ck_decision_log_kind", "kind IN ('status', 'priority', 'merge', 'move', 'rename', 'category')");
                    table.ForeignKey(
                        name: "fk_decision_log_problems_problem_id",
                        column: x => x.problem_id,
                        principalSchema: "feedback",
                        principalTable: "problems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_problems_priority",
                schema: "feedback",
                table: "problems",
                sql: "priority IS NULL OR priority IN ('High', 'Medium', 'Low')");

            migrationBuilder.CreateIndex(
                name: "ix_decision_log_problem_id",
                schema: "feedback",
                table: "decision_log",
                column: "problem_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "decision_log",
                schema: "feedback");

            migrationBuilder.DropCheckConstraint(
                name: "ck_problems_priority",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "priority",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "priority_override_reason",
                schema: "feedback",
                table: "problems");
        }
    }
}
