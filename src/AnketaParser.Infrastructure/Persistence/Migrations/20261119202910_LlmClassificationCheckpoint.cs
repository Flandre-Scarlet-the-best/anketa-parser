using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnketaParser.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LlmClassificationCheckpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Classification",
                schema: "product",
                table: "JobAttempt",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Classification",
                schema: "product",
                table: "JobAttempt");
        }
    }
}