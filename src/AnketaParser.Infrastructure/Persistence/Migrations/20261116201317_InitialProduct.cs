using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnketaParser.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialProduct : Migration
    {
        private static readonly string[] Columns1 = new[] { "OwnerId", "AnonId" };
        private static readonly string[] Columns2 = new[] { "Platform", "NormalizedValue" };
        private static readonly string[] Columns3 = new[] { "ContactId", "RevisionId" };
        private static readonly string[] Columns4 = new[] { "ContactId", "AccountId", "VerifiedAt" };
        private static readonly string[] Columns5 = new[] { "JobId", "Number" };
        private static readonly string[] Columns6 = new[] { "SourceId", "ExternalId" };
        private static readonly string[] Columns7 = new[] { "PostId", "Hash" };
        private static readonly string[] Columns8 = new[] { "RevisionId", "ProcessorVersion" };
        private static readonly string[] Columns9 = new[] { "State", "AvailableAt", "LeaseUntil" };
        private static readonly string[] Columns10 = new[] { "PostId", "State", "CreatedAt" };
        private static readonly string[] Columns11 = new[] { "AnonId", "UnlinkedAt" };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "product");

            migrationBuilder.EnsureSchema(
                name: "private");

            migrationBuilder.CreateTable(
                name: "Anon",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Anon", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BotUser",
                schema: "private",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TechnicalId = table.Column<long>(type: "bigint", nullable: false),
                    Settings = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotUser", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Contact",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    NormalizedValue = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contact", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Source",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Source", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TelegramAccount",
                schema: "private",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TechnicalId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramAccount", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BlacklistEntry",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlacklistEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlacklistEntry_Anon_AnonId",
                        column: x => x.AnonId,
                        principalSchema: "product",
                        principalTable: "Anon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BlacklistEntry_BotUser_OwnerId",
                        column: x => x.OwnerId,
                        principalSchema: "private",
                        principalTable: "BotUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OriginalPost",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Excluded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OriginalPost", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OriginalPost_Source_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "product",
                        principalTable: "Source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContactVerification",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provenance = table.Column<string>(type: "text", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactVerification", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactVerification_Contact_ContactId",
                        column: x => x.ContactId,
                        principalSchema: "product",
                        principalTable: "Contact",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContactVerification_TelegramAccount_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "private",
                        principalTable: "TelegramAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TelegramUsername",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Provenance = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramUsername", x => x.Id);
                    table.CheckConstraint("username_interval", "\"ValidTo\" IS NULL OR \"ValidTo\" >= \"ValidFrom\"");
                    table.ForeignKey(
                        name: "FK_TelegramUsername_TelegramAccount_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "private",
                        principalTable: "TelegramAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PostRevision",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PostId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    RetrievalMetadata = table.Column<string>(type: "jsonb", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostRevision", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostRevision_OriginalPost_PostId",
                        column: x => x.PostId,
                        principalSchema: "product",
                        principalTable: "OriginalPost",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Analysis",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Processor = table.Column<string>(type: "text", nullable: false),
                    SchemaVersion = table.Column<string>(type: "text", nullable: false),
                    Age = table.Column<int>(type: "integer", nullable: true),
                    Location = table.Column<string>(type: "text", nullable: true),
                    GenderDescription = table.Column<string>(type: "text", nullable: true),
                    Interests = table.Column<string>(type: "text", nullable: true),
                    Goals = table.Column<string>(type: "text", nullable: true),
                    Observations = table.Column<string>(type: "jsonb", nullable: false),
                    Assessments = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Analysis", x => x.Id);
                    table.CheckConstraint("age_valid", "\"Age\" IS NULL OR \"Age\" BETWEEN 0 AND 130");
                    table.ForeignKey(
                        name: "FK_Analysis_PostRevision_RevisionId",
                        column: x => x.RevisionId,
                        principalSchema: "product",
                        principalTable: "PostRevision",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContactObservation",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawValue = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    Provenance = table.Column<string>(type: "text", nullable: false),
                    SeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactObservation", x => x.Id);
                    table.CheckConstraint("confidence_valid", "\"Confidence\" BETWEEN 0 AND 1");
                    table.ForeignKey(
                        name: "FK_ContactObservation_Contact_ContactId",
                        column: x => x.ContactId,
                        principalSchema: "product",
                        principalTable: "Contact",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContactObservation_PostRevision_RevisionId",
                        column: x => x.RevisionId,
                        principalSchema: "product",
                        principalTable: "PostRevision",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProcessingJob",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessorVersion = table.Column<string>(type: "text", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FailureCategory = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessingJob", x => x.Id);
                    table.CheckConstraint("attempts_valid", "\"MaxAttempts\" BETWEEN 1 AND 100 AND \"Attempts\" BETWEEN 0 AND \"MaxAttempts\"");
                    table.CheckConstraint("job_state_valid", "\"State\" IN ('pending','running','succeeded','failed')");
                    table.CheckConstraint("lease_valid", "(\"State\" = 'running') = (\"LeaseToken\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ProcessingJob_PostRevision_RevisionId",
                        column: x => x.RevisionId,
                        principalSchema: "product",
                        principalTable: "PostRevision",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Profile",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisId = table.Column<Guid>(type: "uuid", nullable: false),
                    PostId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profile", x => x.Id);
                    table.CheckConstraint("profile_state_valid", "\"State\" BETWEEN 0 AND 4");
                    table.ForeignKey(
                        name: "FK_Profile_Analysis_AnalysisId",
                        column: x => x.AnalysisId,
                        principalSchema: "product",
                        principalTable: "Analysis",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Profile_OriginalPost_PostId",
                        column: x => x.PostId,
                        principalSchema: "product",
                        principalTable: "OriginalPost",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobAttempt",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCategory = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAttempt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobAttempt_ProcessingJob_JobId",
                        column: x => x.JobId,
                        principalSchema: "product",
                        principalTable: "ProcessingJob",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Complaint",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Complaint", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Complaint_BotUser_OwnerId",
                        column: x => x.OwnerId,
                        principalSchema: "private",
                        principalTable: "BotUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Complaint_Contact_ContactId",
                        column: x => x.ContactId,
                        principalSchema: "product",
                        principalTable: "Contact",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Complaint_Profile_ProfileId",
                        column: x => x.ProfileId,
                        principalSchema: "product",
                        principalTable: "Profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProfileLink",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Confidence = table.Column<int>(type: "integer", nullable: false),
                    Provenance = table.Column<string>(type: "text", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UnlinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UnlinkReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileLink", x => x.Id);
                    table.CheckConstraint("link_confidence_valid", "\"Confidence\" BETWEEN 0 AND 1");
                    table.ForeignKey(
                        name: "FK_ProfileLink_Anon_AnonId",
                        column: x => x.AnonId,
                        principalSchema: "product",
                        principalTable: "Anon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProfileLink_Contact_ContactId",
                        column: x => x.ContactId,
                        principalSchema: "product",
                        principalTable: "Contact",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProfileLink_Profile_ProfileId",
                        column: x => x.ProfileId,
                        principalSchema: "product",
                        principalTable: "Profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ModerationDecision",
                schema: "product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComplaintId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    OwnershipEvidence = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationDecision", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModerationDecision_Complaint_ComplaintId",
                        column: x => x.ComplaintId,
                        principalSchema: "product",
                        principalTable: "Complaint",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ModerationDecision_Profile_ProfileId",
                        column: x => x.ProfileId,
                        principalSchema: "product",
                        principalTable: "Profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Analysis_RevisionId",
                schema: "product",
                table: "Analysis",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_BlacklistEntry_AnonId",
                schema: "product",
                table: "BlacklistEntry",
                column: "AnonId");

            migrationBuilder.CreateIndex(
                name: "IX_BlacklistEntry_OwnerId_AnonId",
                schema: "product",
                table: "BlacklistEntry",
                columns: Columns1,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BotUser_TechnicalId",
                schema: "private",
                table: "BotUser",
                column: "TechnicalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Complaint_ContactId",
                schema: "product",
                table: "Complaint",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Complaint_OwnerId",
                schema: "product",
                table: "Complaint",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Complaint_ProfileId",
                schema: "product",
                table: "Complaint",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Contact_Platform_NormalizedValue",
                schema: "product",
                table: "Contact",
                columns: Columns2,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContactObservation_ContactId_RevisionId",
                schema: "product",
                table: "ContactObservation",
                columns: Columns3);

            migrationBuilder.CreateIndex(
                name: "IX_ContactObservation_RevisionId",
                schema: "product",
                table: "ContactObservation",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactVerification_AccountId",
                schema: "product",
                table: "ContactVerification",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactVerification_ContactId_AccountId_VerifiedAt",
                schema: "product",
                table: "ContactVerification",
                columns: Columns4);

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempt_JobId_Number",
                schema: "product",
                table: "JobAttempt",
                columns: Columns5,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModerationDecision_ComplaintId",
                schema: "product",
                table: "ModerationDecision",
                column: "ComplaintId");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationDecision_ProfileId",
                schema: "product",
                table: "ModerationDecision",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_OriginalPost_SourceId_ExternalId",
                schema: "product",
                table: "OriginalPost",
                columns: Columns6,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostRevision_PostId_Hash",
                schema: "product",
                table: "PostRevision",
                columns: Columns7,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJob_RevisionId_ProcessorVersion",
                schema: "product",
                table: "ProcessingJob",
                columns: Columns8,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJob_State_AvailableAt_LeaseUntil",
                schema: "product",
                table: "ProcessingJob",
                columns: Columns9);

            migrationBuilder.CreateIndex(
                name: "IX_Profile_AnalysisId",
                schema: "product",
                table: "Profile",
                column: "AnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_Profile_PostId_State_CreatedAt",
                schema: "product",
                table: "Profile",
                columns: Columns10);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileLink_AnonId_UnlinkedAt",
                schema: "product",
                table: "ProfileLink",
                columns: Columns11);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileLink_ContactId",
                schema: "product",
                table: "ProfileLink",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileLink_ProfileId",
                schema: "product",
                table: "ProfileLink",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Source_Key",
                schema: "product",
                table: "Source",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramAccount_TechnicalId",
                schema: "private",
                table: "TelegramAccount",
                column: "TechnicalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramUsername_AccountId",
                schema: "product",
                table: "TelegramUsername",
                column: "AccountId",
                unique: true,
                filter: "\"ValidTo\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramUsername_Username",
                schema: "product",
                table: "TelegramUsername",
                column: "Username",
                unique: true,
                filter: "\"ValidTo\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlacklistEntry",
                schema: "product");

            migrationBuilder.DropTable(
                name: "ContactObservation",
                schema: "product");

            migrationBuilder.DropTable(
                name: "ContactVerification",
                schema: "product");

            migrationBuilder.DropTable(
                name: "JobAttempt",
                schema: "product");

            migrationBuilder.DropTable(
                name: "ModerationDecision",
                schema: "product");

            migrationBuilder.DropTable(
                name: "ProfileLink",
                schema: "product");

            migrationBuilder.DropTable(
                name: "TelegramUsername",
                schema: "product");

            migrationBuilder.DropTable(
                name: "ProcessingJob",
                schema: "product");

            migrationBuilder.DropTable(
                name: "Complaint",
                schema: "product");

            migrationBuilder.DropTable(
                name: "Anon",
                schema: "product");

            migrationBuilder.DropTable(
                name: "TelegramAccount",
                schema: "private");

            migrationBuilder.DropTable(
                name: "BotUser",
                schema: "private");

            migrationBuilder.DropTable(
                name: "Contact",
                schema: "product");

            migrationBuilder.DropTable(
                name: "Profile",
                schema: "product");

            migrationBuilder.DropTable(
                name: "Analysis",
                schema: "product");

            migrationBuilder.DropTable(
                name: "PostRevision",
                schema: "product");

            migrationBuilder.DropTable(
                name: "OriginalPost",
                schema: "product");

            migrationBuilder.DropTable(
                name: "Source",
                schema: "product");
        }
    }
}