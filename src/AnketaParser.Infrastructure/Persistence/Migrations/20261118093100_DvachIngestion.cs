using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnketaParser.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DvachIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IngestionAmbiguous",
                schema: "product",
                table: "PostRevision",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "IngestionContacts",
                schema: "product",
                table: "PostRevision",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IngestionReason",
                schema: "product",
                table: "PostRevision",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IngestionReply",
                schema: "product",
                table: "PostRevision",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IngestedRevisionId",
                schema: "product",
                table: "OriginalPost",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SourceScan",
                schema: "product",
                columns: table => new
                {
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: false),
                    Failure = table.Column<string>(type: "text", nullable: true),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    Pending = table.Column<string>(type: "text", nullable: true),
                    Applied = table.Column<int>(type: "integer", nullable: false),
                    Metrics = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceScan", x => x.SourceId);
                    table.ForeignKey(
                        name: "FK_SourceScan_Source_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "product",
                        principalTable: "Source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OriginalPost_IngestedRevisionId",
                schema: "product",
                table: "OriginalPost",
                column: "IngestedRevisionId");

            migrationBuilder.AddForeignKey(
                name: "FK_OriginalPost_PostRevision_IngestedRevisionId",
                schema: "product",
                table: "OriginalPost",
                column: "IngestedRevisionId",
                principalSchema: "product",
                principalTable: "PostRevision",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OriginalPost_PostRevision_IngestedRevisionId",
                schema: "product",
                table: "OriginalPost");

            migrationBuilder.DropTable(
                name: "SourceScan",
                schema: "product");

            migrationBuilder.DropIndex(
                name: "IX_OriginalPost_IngestedRevisionId",
                schema: "product",
                table: "OriginalPost");

            migrationBuilder.DropColumn(
                name: "IngestionAmbiguous",
                schema: "product",
                table: "PostRevision");

            migrationBuilder.DropColumn(
                name: "IngestionContacts",
                schema: "product",
                table: "PostRevision");

            migrationBuilder.DropColumn(
                name: "IngestionReason",
                schema: "product",
                table: "PostRevision");

            migrationBuilder.DropColumn(
                name: "IngestionReply",
                schema: "product",
                table: "PostRevision");

            migrationBuilder.DropColumn(
                name: "IngestedRevisionId",
                schema: "product",
                table: "OriginalPost");
        }
    }
}