using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubIssue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "github_checked_at",
                schema: "feedback",
                table: "problems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "github_issue_number",
                schema: "feedback",
                table: "problems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_issue_state",
                schema: "feedback",
                table: "problems",
                type: "character varying(6)",
                maxLength: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_issue_url",
                schema: "feedback",
                table: "problems",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "github_checked_at",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "github_issue_number",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "github_issue_state",
                schema: "feedback",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "github_issue_url",
                schema: "feedback",
                table: "problems");
        }
    }
}
