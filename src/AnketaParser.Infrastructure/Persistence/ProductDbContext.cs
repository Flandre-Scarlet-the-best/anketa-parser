using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace AnketaParser.Infrastructure.Persistence;

public sealed class ProductDbContext(DbContextOptions<ProductDbContext> options) : DbContext(options)
{
    /// <summary>Прикладная схема продукта: таблицы, история миграций и их зависимости.</summary>
    public const string Schema = "product";

    /// <summary>Схема служебных идентичностей, закрытая от публичной выдачи.</summary>
    public const string PrivateSchema = "private";

    /// <summary>История миграций принадлежит схеме продукта, а не public: архив ограничен схемами продукта.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>Единственная точка настройки свойств Npgsql для контекста продукта.</summary>
    public static void ConfigureProvider(NpgsqlDbContextOptionsBuilder provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.MigrationsHistoryTable(MigrationsHistoryTable, Schema);
    }

    /// <summary>Собирает опции так, чтобы история миграций читалась и создавалась в схеме продукта.</summary>
    public static DbContextOptions<ProductDbContext> BuildOptions(string? connectionString) =>
        new DbContextOptionsBuilder<ProductDbContext>().UseNpgsql(connectionString, ConfigureProvider).Options;

    /// <summary>Создаёт контекст с согласованным размещением истории миграций.</summary>
    public static ProductDbContext Create(string connectionString) => new(BuildOptions(connectionString));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<Source>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<OriginalPost>().HasIndex(x => new { x.SourceId, x.ExternalId }).IsUnique();
        modelBuilder.Entity<SourceScan>().HasKey(x => x.SourceId);
        modelBuilder.Entity<PostRevision>().HasIndex(x => new { x.PostId, x.Hash }).IsUnique();
        modelBuilder.Entity<Contact>().HasIndex(x => new { x.Platform, x.NormalizedValue }).IsUnique();
        modelBuilder.Entity<TelegramAccount>().ToTable("TelegramAccount", PrivateSchema);
        modelBuilder.Entity<TelegramAccount>().HasIndex(x => x.TechnicalId).IsUnique();
        modelBuilder.Entity<BotUser>().ToTable("BotUser", PrivateSchema);
        modelBuilder.Entity<BotUser>().HasIndex(x => x.TechnicalId).IsUnique();
        modelBuilder.Entity<TelegramUsername>().HasIndex(x => x.Username).IsUnique().HasFilter("\"ValidTo\" IS NULL");
        modelBuilder.Entity<TelegramUsername>().HasIndex(x => x.AccountId).IsUnique().HasFilter("\"ValidTo\" IS NULL");
        modelBuilder.Entity<ContactVerification>().HasIndex(x => new { x.ContactId, x.AccountId, x.VerifiedAt });
        modelBuilder.Entity<BlacklistEntry>().HasIndex(x => new { x.OwnerId, x.AnonId }).IsUnique();
        modelBuilder.Entity<ProcessingJob>().HasIndex(x => new { x.RevisionId, x.ProcessorVersion }).IsUnique();
        modelBuilder.Entity<ProcessingJob>().HasIndex(x => new { x.RevisionId, x.LlmVersion }).IsUnique().HasFilter("\"LlmVersion\" IS NOT NULL");
        modelBuilder.Entity<ProcessingJob>().HasIndex(x => new { x.LlmVersion, x.State, x.LeaseUntil });
        modelBuilder.Entity<ProcessingJob>().HasIndex(x => new { x.State, x.AvailableAt, x.LeaseUntil });
        modelBuilder.Entity<JobAttempt>().HasIndex(x => new { x.JobId, x.Number }).IsUnique();
        modelBuilder.Entity<JobAttempt>().HasIndex(x => x.Token).IsUnique();
        modelBuilder.Entity<JobAttempt>().HasIndex(x => new { x.BudgetKey, x.StartedAt }).HasFilter("\"BudgetKey\" IS NOT NULL");
        modelBuilder.Entity<Analysis>().HasIndex(x => x.ProcessingJobId).IsUnique().HasFilter("\"ProcessingJobId\" IS NOT NULL");
        modelBuilder.Entity<Analysis>().HasIndex(x => x.RevisionId);
        modelBuilder.Entity<Analysis>().HasIndex(x => new { x.RevisionId, x.PipelineVersion }).IsUnique().HasFilter("\"PipelineVersion\" IS NOT NULL");
        modelBuilder.Entity<Profile>().HasIndex(x => new { x.PostId, x.State, x.CreatedAt });
        modelBuilder.Entity<ProfileLink>().HasIndex(x => new { x.AnonId, x.UnlinkedAt });
        modelBuilder.Entity<ContactObservation>().HasIndex(x => new { x.ContactId, x.RevisionId });
        Foreign<OriginalPost, Source>(modelBuilder, "SourceId");
        Foreign<SourceScan, Source>(modelBuilder, "SourceId");
        Foreign<OriginalPost, PostRevision>(modelBuilder, "IngestedRevisionId");
        Foreign<PostRevision, OriginalPost>(modelBuilder, "PostId");
        Foreign<Analysis, PostRevision>(modelBuilder, "RevisionId");
        Foreign<Analysis, ProcessingJob>(modelBuilder, "ProcessingJobId");
        Foreign<Profile, Analysis>(modelBuilder, "AnalysisId");
        Foreign<Profile, OriginalPost>(modelBuilder, "PostId");
        Foreign<ContactObservation, Contact>(modelBuilder, "ContactId");
        Foreign<ContactObservation, PostRevision>(modelBuilder, "RevisionId");
        Foreign<TelegramUsername, TelegramAccount>(modelBuilder, "AccountId");
        Foreign<ContactVerification, TelegramAccount>(modelBuilder, "AccountId");
        Foreign<ContactVerification, Contact>(modelBuilder, "ContactId");
        Foreign<ProfileLink, Profile>(modelBuilder, "ProfileId");
        Foreign<ProfileLink, Contact>(modelBuilder, "ContactId");
        Foreign<ProfileLink, Anon>(modelBuilder, "AnonId");
        Foreign<ProfileLink, ContactVerification>(modelBuilder, "VerificationId");
        Foreign<BlacklistEntry, BotUser>(modelBuilder, "OwnerId");
        Foreign<BlacklistEntry, Anon>(modelBuilder, "AnonId");
        Foreign<Complaint, BotUser>(modelBuilder, "OwnerId");
        Foreign<Complaint, Profile>(modelBuilder, "ProfileId");
        Foreign<Complaint, Contact>(modelBuilder, "ContactId");
        Foreign<ModerationDecision, Profile>(modelBuilder, "ProfileId");
        Foreign<ModerationDecision, Complaint>(modelBuilder, "ComplaintId");
        Foreign<ProcessingJob, PostRevision>(modelBuilder, "RevisionId");
        Foreign<JobAttempt, ProcessingJob>(modelBuilder, "JobId");
        modelBuilder.Entity<Analysis>().Property(x => x.Observations).HasColumnType("jsonb");
        modelBuilder.Entity<Analysis>().Property(x => x.Assessments).HasColumnType("jsonb");
        modelBuilder.Entity<PostRevision>().Property(x => x.RetrievalMetadata).HasColumnType("jsonb");
        modelBuilder.Entity<BotUser>().Property(x => x.Settings).HasColumnType("jsonb");
        modelBuilder.Entity<ContactObservation>().Property(x => x.Confidence).HasPrecision(4, 3);
        modelBuilder.Entity<Analysis>().ToTable(t =>
        {
            t.HasCheckConstraint("age_valid", "\"Age\" IS NULL OR \"Age\" BETWEEN 0 AND 130");
            t.HasCheckConstraint("llm_decision_valid", """
                ("ProcessingJobId" IS NULL AND "PipelineVersion" IS NULL AND "SemanticClass" IS NULL AND "SemanticReason" IS NULL AND "Model" IS NULL)
                OR ("ProcessingJobId" IS NOT NULL AND "PipelineVersion" IS NOT NULL AND "SemanticClass" IS NOT NULL
                AND "SemanticClass" IN ('profile','not_profile','uncertain') AND "SemanticReason" IS NOT NULL AND "Model" IS NOT NULL)
                """);
        });
        modelBuilder.Entity<ContactObservation>().ToTable(t => t.HasCheckConstraint("confidence_valid", "\"Confidence\" BETWEEN 0 AND 1"));
        modelBuilder.Entity<Profile>().ToTable(t => t.HasCheckConstraint("profile_state_valid", "\"State\" BETWEEN 0 AND 4"));
        modelBuilder.Entity<ProfileLink>().ToTable(t => t.HasCheckConstraint("link_confidence_valid", "\"Confidence\" BETWEEN 0 AND 1"));
        modelBuilder.Entity<TelegramUsername>().ToTable(t => t.HasCheckConstraint("username_interval", "\"ValidTo\" IS NULL OR \"ValidTo\" >= \"ValidFrom\""));
        modelBuilder.Entity<ProcessingJob>().ToTable(t =>
        {
            t.HasCheckConstraint("attempts_valid", "\"MaxAttempts\" BETWEEN 1 AND 100 AND \"Attempts\" BETWEEN 0 AND \"MaxAttempts\"");
            t.HasCheckConstraint("job_state_valid", "\"State\" IN ('pending','running','succeeded','failed')");
            t.HasCheckConstraint("lease_valid", "(\"State\" = 'running') = (\"LeaseToken\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL)");
        });
    }

    private static void Foreign<T, TPrincipal>(ModelBuilder builder, string property)
        where T : class where TPrincipal : class =>
        builder.Entity<T>().HasOne<TPrincipal>().WithMany().HasForeignKey(property).OnDelete(DeleteBehavior.Restrict);
}

/// <summary>Создание модели для инструментов EF Core не требует подключения или секрета.</summary>
public sealed class ProductDesignTimeFactory : IDesignTimeDbContextFactory<ProductDbContext>
{
    public ProductDbContext CreateDbContext(string[] args) => new(ProductDbContext.BuildOptions(connectionString: null));
}