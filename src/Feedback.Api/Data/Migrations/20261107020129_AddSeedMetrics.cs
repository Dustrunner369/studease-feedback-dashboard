using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Feedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeedMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "seed_metrics",
                schema: "feedback",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    duplicate_catch_rate = table.Column<double>(type: "double precision", nullable: true),
                    grouping_accuracy = table.Column<double>(type: "double precision", nullable: true),
                    ai_problem_count = table.Column<int>(type: "integer", nullable: false),
                    intended_problem_count = table.Column<int>(type: "integer", nullable: false),
                    failed_matches = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seed_metrics", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "seed_metrics",
                schema: "feedback");
        }
    }
}
