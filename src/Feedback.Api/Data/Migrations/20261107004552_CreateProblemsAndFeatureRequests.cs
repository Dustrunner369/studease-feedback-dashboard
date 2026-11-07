using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace Feedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreateProblemsAndFeatureRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "feedback");

            migrationBuilder.CreateTable(
                name: "problems",
                schema: "feedback",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_problems", x => x.id);
                    table.CheckConstraint("ck_problems_category", "category IN ('Finding spots', 'Adding & editing spots', 'Spot details', 'Visits', 'Profile & account', 'Social', 'Other')");
                    table.CheckConstraint("ck_problems_status", "status IN ('New', 'Under review', 'Planned', 'In progress', 'Shipped', 'Won''t do')");
                });

            migrationBuilder.CreateTable(
                name: "feature_requests",
                schema: "feedback",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    problem_id = table.Column<long>(type: "bigint", nullable: true),
                    embedding = table.Column<Vector>(type: "vector(1536)", nullable: true),
                    match_reason = table.Column<string>(type: "text", nullable: true),
                    is_bundled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feature_requests", x => x.id);
                    table.CheckConstraint("ck_feature_requests_category", "category IN ('Finding spots', 'Adding & editing spots', 'Spot details', 'Visits', 'Profile & account', 'Social', 'Other')");
                    table.ForeignKey(
                        name: "fk_feature_requests_problems_problem_id",
                        column: x => x.problem_id,
                        principalSchema: "feedback",
                        principalTable: "problems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_feature_requests_created_at",
                schema: "feedback",
                table: "feature_requests",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_feature_requests_problem_id",
                schema: "feedback",
                table: "feature_requests",
                column: "problem_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feature_requests",
                schema: "feedback");

            migrationBuilder.DropTable(
                name: "problems",
                schema: "feedback");
        }
    }
}
