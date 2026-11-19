using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnketaParser.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LlmProfileAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LlmVersion",
                schema: "product",
                table: "ProcessingJob",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BudgetKey",
                schema: "product",
                table: "JobAttempt",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CachedInputTokens",
                schema: "product",
                table: "JobAttempt",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ElapsedMilliseconds",
                schema: "product",
                table: "JobAttempt",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InputTokens",
                schema: "product",
                table: "JobAttempt",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MeasuredRequests",
                schema: "product",
                table: "JobAttempt",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "OutputTokens",
                schema: "product",
                table: "JobAttempt",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReservedRequests",
                schema: "product",
                table: "JobAttempt",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                schema: "product",
                table: "Analysis",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PipelineVersion",
                schema: "product",
                table: "Analysis",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProcessingJobId",
                schema: "product",
                table: "Analysis",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SemanticClass",
                schema: "product",
                table: "Analysis",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SemanticReason",
                schema: "product",
                table: "Analysis",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJob_LlmVersion_State_LeaseUntil",
                schema: "product",
                table: "ProcessingJob",
                columns: ["LlmVersion", "State", "LeaseUntil"]);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJob_RevisionId_LlmVersion",
                schema: "product",
                table: "ProcessingJob",
                columns: ["RevisionId", "LlmVersion"],
                unique: true,
                filter: "\"LlmVersion\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempt_BudgetKey_StartedAt",
                schema: "product",
                table: "JobAttempt",
                columns: ["BudgetKey", "StartedAt"],
                filter: "\"BudgetKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Analysis_ProcessingJobId",
                schema: "product",
                table: "Analysis",
                column: "ProcessingJobId",
                unique: true,
                filter: "\"ProcessingJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Analysis_RevisionId_PipelineVersion",
                schema: "product",
                table: "Analysis",
                columns: ["RevisionId", "PipelineVersion"],
                unique: true,
                filter: "\"PipelineVersion\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "llm_decision_valid",
                schema: "product",
                table: "Analysis",
                sql: "(\"ProcessingJobId\" IS NULL AND \"PipelineVersion\" IS NULL AND \"SemanticClass\" IS NULL AND \"SemanticReason\" IS NULL AND \"Model\" IS NULL)\nOR (\"ProcessingJobId\" IS NOT NULL AND \"PipelineVersion\" IS NOT NULL AND \"SemanticClass\" IS NOT NULL\nAND \"SemanticClass\" IN ('profile','not_profile','uncertain') AND \"SemanticReason\" IS NOT NULL AND \"Model\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Analysis_ProcessingJob_ProcessingJobId",
                schema: "product",
                table: "Analysis",
                column: "ProcessingJobId",
                principalSchema: "product",
                principalTable: "ProcessingJob",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE FUNCTION product.guard_llm_profile() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE decision text;
                BEGIN
                  SELECT "SemanticClass" INTO decision FROM product."Analysis"
                  WHERE "Id"=NEW."AnalysisId" FOR UPDATE;
                  IF decision IS NOT NULL AND (decision <> 'profile' OR EXISTS (
                    SELECT 1 FROM product."Profile" WHERE "AnalysisId"=NEW."AnalysisId" AND "Id"<>NEW."Id")) THEN
                    RAISE EXCEPTION 'invalid_llm_profile' USING ERRCODE='23514';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER guard_profile_llm BEFORE INSERT OR UPDATE OF "AnalysisId" ON product."Profile"
                FOR EACH ROW EXECUTE FUNCTION product.guard_llm_profile();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER guard_profile_llm ON product."Profile";
                DROP FUNCTION product.guard_llm_profile();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Analysis_ProcessingJob_ProcessingJobId",
                schema: "product",
                table: "Analysis");

            migrationBuilder.DropIndex(
                name: "IX_ProcessingJob_LlmVersion_State_LeaseUntil",
                schema: "product",
                table: "ProcessingJob");

            migrationBuilder.DropIndex(
                name: "IX_ProcessingJob_RevisionId_LlmVersion",
                schema: "product",
                table: "ProcessingJob");

            migrationBuilder.DropIndex(
                name: "IX_JobAttempt_BudgetKey_StartedAt",
                schema: "product",
                table: "JobAttempt");

            migrationBuilder.DropIndex(
                name: "IX_Analysis_ProcessingJobId",
                schema: "product",
                table: "Analysis");

            migrationBuilder.DropIndex(
                name: "IX_Analysis_RevisionId_PipelineVersion",
                schema: "product",
                table: "Analysis");

            migrationBuilder.DropCheckConstraint(
                name: "llm_decision_valid",
                schema: "product",
                table: "Analysis");

            migrationBuilder.DropColumn(
                name: "LlmVersion",
                schema: "product",
                table: "ProcessingJob");

            migrationBuilder.DropColumn(
                name: "BudgetKey",
                schema: "product",
                table: "JobAttempt");

            migrationBuilder.DropColumn(
                name: "CachedInputTokens",
                schema: "product",
                table: "JobAttempt");

            migrationBuilder.DropColumn(
                name: "ElapsedMilliseconds",
                schema: "product",
                table: "JobAttempt");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                schema: "product",
                table: "JobAttempt");

            migrationBuilder.DropColumn(
                name: "MeasuredRequests",
                schema: "product",
                table: "JobAttempt");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                schema: "product",
                table: "JobAttempt");

            migrationBuilder.DropColumn(
                name: "ReservedRequests",
                schema: "product",
                table: "JobAttempt");

            migrationBuilder.DropColumn(
                name: "Model",
                schema: "product",
                table: "Analysis");

            migrationBuilder.DropColumn(
                name: "PipelineVersion",
                schema: "product",
                table: "Analysis");

            migrationBuilder.DropColumn(
                name: "ProcessingJobId",
                schema: "product",
                table: "Analysis");

            migrationBuilder.DropColumn(
                name: "SemanticClass",
                schema: "product",
                table: "Analysis");

            migrationBuilder.DropColumn(
                name: "SemanticReason",
                schema: "product",
                table: "Analysis");

        }
    }
}