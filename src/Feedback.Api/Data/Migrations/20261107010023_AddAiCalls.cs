using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Feedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_calls",
                schema: "feedback",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    step = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    failure_kind = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    problem_id = table.Column<long>(type: "bigint", nullable: true),
                    feature_request_id = table.Column<long>(type: "bigint", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric(12,8)", precision: 12, scale: 8, nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_calls", x => x.id);
                    table.CheckConstraint("ck_ai_calls_failure_kind", "failure_kind IS NULL OR failure_kind IN ('Timeout', 'API error', 'Invalid output')");
                    table.CheckConstraint("ck_ai_calls_status", "status IN ('OK', 'Failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_calls_created_at",
                schema: "feedback",
                table: "ai_calls",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_calls_problem_id",
                schema: "feedback",
                table: "ai_calls",
                column: "problem_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_calls",
                schema: "feedback");
        }
    }
}
