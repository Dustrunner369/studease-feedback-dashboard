using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreateUpvotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "upvotes",
                schema: "feedback",
                columns: table => new
                {
                    feature_request_id = table.Column<long>(type: "bigint", nullable: false),
                    browser_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_upvotes", x => new { x.feature_request_id, x.browser_token });
                    table.ForeignKey(
                        name: "fk_upvotes_feature_requests_feature_request_id",
                        column: x => x.feature_request_id,
                        principalSchema: "feedback",
                        principalTable: "feature_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "upvotes",
                schema: "feedback");
        }
    }
}
