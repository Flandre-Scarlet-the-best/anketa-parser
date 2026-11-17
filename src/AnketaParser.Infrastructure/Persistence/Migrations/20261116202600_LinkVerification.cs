using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnketaParser.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LinkVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "VerificationId",
                schema: "product",
                table: "ProfileLink",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileLink_VerificationId",
                schema: "product",
                table: "ProfileLink",
                column: "VerificationId");

            migrationBuilder.AddForeignKey(
                name: "FK_ProfileLink_ContactVerification_VerificationId",
                schema: "product",
                table: "ProfileLink",
                column: "VerificationId",
                principalSchema: "product",
                principalTable: "ContactVerification",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProfileLink_ContactVerification_VerificationId",
                schema: "product",
                table: "ProfileLink");

            migrationBuilder.DropIndex(
                name: "IX_ProfileLink_VerificationId",
                schema: "product",
                table: "ProfileLink");

            migrationBuilder.DropColumn(
                name: "VerificationId",
                schema: "product",
                table: "ProfileLink");
        }
    }
}