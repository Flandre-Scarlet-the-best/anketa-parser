using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AnketaParser.Core.Persistence;

using Microsoft.EntityFrameworkCore;

namespace AnketaParser.Infrastructure.Persistence;

/// <summary>Один экземпляр на операционную область. Транзакционные блокировки сериализуют изменения одной идентичности.</summary>
public sealed class ProductStore(ProductDbContext db) : IProductStore
{
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    // Сколько кандидатов рассматривается за один захват: пост, закрытый другой
    // транзакцией, пропускается через SKIP LOCKED и не лишает захвата остальных.
    private const int ClaimCandidates = 8;

    private async Task LockAsync(string key, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct).ConfigureAwait(false);

    internal Task LockSourceAsync(string key, CancellationToken ct) => LockAsync("source:" + key, ct);

    /// <summary>
    /// Блокировка строки поста. Триггер guard_profile тоже блокирует OriginalPost, поэтому изменение
    /// Profile обязано брать эту строку первой: иначе ArchiveAsync (пост → профиль) даёт взаимную блокировку.
    /// </summary>
    private async Task LockPostAsync(Guid postId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM product."OriginalPost" WHERE "Id"={postId} FOR UPDATE""", ct).ConfigureAwait(false);

    /// <summary>Порядок блокировок: пост, затем профиль.</summary>
    private async Task<Profile> LockProfileAsync(Guid profileId, CancellationToken ct)
    {
        var postId = await db.Set<Profile>().AsNoTracking().Where(x => x.Id == profileId)
            .Select(x => x.PostId).SingleAsync(ct).ConfigureAwait(false);
        await LockPostAsync(postId, ct).ConfigureAwait(false);
        var profile = await db.Set<Profile>().SingleAsync(x => x.Id == profileId, ct).ConfigureAwait(false);
        await db.Entry(profile).ReloadAsync(ct).ConfigureAwait(false);
        return profile;
    }

    public async Task<Guid> ImportAsync(PostInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ExternalId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var source = await EnsureSourceAsync(input.Source, input.Source, cancellationToken).ConfigureAwait(false);
        var (_, revision, _, _) = await ImportPostAsync(source.Id, input, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revision.Id;
    }

    internal async Task<Source> EnsureSourceAsync(string key, string url, CancellationToken ct)
    {
        await LockSourceAsync(key, ct).ConfigureAwait(false);
        var source = await db.Set<Source>().SingleOrDefaultAsync(x => x.Key == key, ct).ConfigureAwait(false);
        if (source is null)
        {
            source = new Source { Key = key, Url = url };
            db.Add(source);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        return source;
    }

    internal async Task<(OriginalPost Post, PostRevision Revision, bool NewPost, bool NewRevision)> ImportPostAsync(
        Guid sourceId, PostInput input, CancellationToken ct)
    {
        var post = await db.Set<OriginalPost>().SingleOrDefaultAsync(x => x.SourceId == sourceId && x.ExternalId == input.ExternalId, ct).ConfigureAwait(false);
        var newPost = post is null;
        if (post is null)
        {
            post = new OriginalPost { SourceId = sourceId, ExternalId = input.ExternalId, Url = input.Url, PublishedAt = input.PublishedAt };
            db.Add(post);
        }
        else
        {
            await LockPostAsync(post.Id, ct).ConfigureAwait(false);
            await db.Entry(post).ReloadAsync(ct).ConfigureAwait(false);
        }
        post.LastSeenAt = Now;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Content)));
        var revision = await db.Set<PostRevision>().SingleOrDefaultAsync(x => x.PostId == post.Id && x.Hash == hash, ct).ConfigureAwait(false);
        var newRevision = revision is null;
        if (revision is null)
        {
            revision = new PostRevision { PostId = post.Id, Hash = hash, Content = input.Content, RetrievalMetadata = input.RetrievalMetadata, ReceivedAt = Now };
            db.Add(revision);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (post, revision, newPost, newRevision);
    }

    public async Task<Guid> AnalyzeAsync(AnalysisInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var revision = await db.Set<PostRevision>().AsNoTracking().SingleAsync(x => x.Id == input.RevisionId, cancellationToken).ConfigureAwait(false);
        await LockPostAsync(revision.PostId, cancellationToken).ConfigureAwait(false);
        await RequireCurrentIngestedRevisionAsync(revision.PostId, revision.Id, cancellationToken).ConfigureAwait(false);
        var analysis = new Analysis
        {
            RevisionId = input.RevisionId,
            Processor = input.Processor,
            SchemaVersion = input.SchemaVersion,
            Age = input.Age,
            Location = input.Location,
            GenderDescription = input.GenderDescription,
            Interests = input.Interests,
            Goals = input.Goals,
            Observations = input.Observations,
            Assessments = input.Assessments,
            CreatedAt = Now,
        };
        var profile = new Profile { AnalysisId = analysis.Id, PostId = revision.PostId, CreatedAt = Now };
        db.Add(analysis);
        db.Add(profile);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return profile.Id;
    }

    private async Task RequireCurrentIngestedRevisionAsync(Guid postId, Guid revisionId, CancellationToken ct)
    {
        if (await db.Set<OriginalPost>().AnyAsync(p => p.Id == postId && p.IngestedRevisionId != null &&
            (p.Excluded || p.IngestedRevisionId != revisionId), ct).ConfigureAwait(false))
            throw new InvalidOperationException("Ревизия исходного поста устарела или исключена.");
    }

    public async Task<Guid> RegisterContactAsync(ContactInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.NormalizedValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Provenance);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var id = await RegisterObservationAsync(input, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    internal async Task<Guid> RegisterObservationAsync(ContactInput input, CancellationToken cancellationToken)
    {
        await LockAsync("contact:" + input.Platform + ":" + input.NormalizedValue, cancellationToken).ConfigureAwait(false);
        var contact = await db.Set<Contact>().SingleOrDefaultAsync(x => x.Platform == input.Platform && x.NormalizedValue == input.NormalizedValue, cancellationToken).ConfigureAwait(false);
        if (contact is null)
        {
            contact = new Contact { Platform = input.Platform, NormalizedValue = input.NormalizedValue };
            db.Add(contact);
        }
        db.Add(new ContactObservation { ContactId = contact.Id, RevisionId = input.RevisionId, RawValue = input.RawValue, Confidence = input.Confidence, Provenance = input.Provenance, SeenAt = Now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return contact.Id;
    }

    public async Task<Guid> ConfirmTelegramAsync(Guid contactId, long technicalId, string? username, string provenance, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provenance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(technicalId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Общая блокировка нужна для атомарной передачи username между аккаунтами.
        await LockAsync("telegram-identities", cancellationToken).ConfigureAwait(false);
        var contact = await db.Set<Contact>().SingleAsync(x => x.Id == contactId, cancellationToken).ConfigureAwait(false);
        if (contact.Platform != "telegram") throw new InvalidOperationException("Подтверждение Telegram недопустимо для другой платформы.");
        var account = await db.Set<TelegramAccount>().SingleOrDefaultAsync(x => x.TechnicalId == technicalId, cancellationToken).ConfigureAwait(false);
        if (account is null) { account = new TelegramAccount { TechnicalId = technicalId }; db.Add(account); }
        username = username?.TrimStart('@').ToLowerInvariant();
        // Пустое значение означает «имя не передано»: отсутствие сведений не доказывает смену имени,
        // поэтому история username не меняется, а новое доказательство сохраняется отдельной проверкой.
        var requested = string.IsNullOrWhiteSpace(username) ? null : username;
        var open = requested is null
            ? await db.Set<TelegramUsername>().Where(x => x.ValidTo == null && x.AccountId == account.Id).ToListAsync(cancellationToken).ConfigureAwait(false)
            : await db.Set<TelegramUsername>().Where(x => x.ValidTo == null && (x.AccountId == account.Id || x.Username == requested)).ToListAsync(cancellationToken).ConfigureAwait(false);
        // Непрерывный интервал того же аккаунта с тем же именем не закрывается: подтверждение без смены.
        var continuous = requested is null ? null : open.FirstOrDefault(x => x.AccountId == account.Id && x.Username == requested);
        if (requested is not null)
        {
            var superseded = open.Where(x => !ReferenceEquals(x, continuous)).ToArray();
            if (superseded.Length > 0)
            {
                foreach (var old in superseded) old.ValidTo = Now;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            if (continuous is null) db.Add(new TelegramUsername { AccountId = account.Id, Username = requested, ValidFrom = Now, Provenance = provenance });
        }
        db.Add(new ContactVerification { ContactId = contactId, AccountId = account.Id, Provenance = provenance, VerifiedAt = Now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return account.Id;
    }

    public async Task<ProfileLinkResult> LinkAsync(Guid profileId, Guid contactId, Guid anonId, LinkConfidence confidence, string provenance, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provenance);
        if (anonId == Guid.Empty)
        {
            var anon = new Anon { CreatedAt = Now };
            db.Add(anon);
            anonId = anon.Id;
        }
        var verificationId = confidence == LinkConfidence.VerifiedAccount
            ? await db.Set<ContactVerification>().Where(x => x.ContactId == contactId).OrderByDescending(x => x.VerifiedAt).ThenByDescending(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var link = new ProfileLink { ProfileId = profileId, ContactId = contactId, AnonId = anonId, Confidence = confidence, VerificationId = verificationId, Provenance = provenance, LinkedAt = Now };
        db.Add(link);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(link.Id, anonId);
    }

    public async Task UnlinkAsync(Guid linkId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync("link:" + linkId, cancellationToken).ConfigureAwait(false);
        var link = await db.Set<ProfileLink>().SingleAsync(x => x.Id == linkId, cancellationToken).ConfigureAwait(false);
        await db.Entry(link).ReloadAsync(cancellationToken).ConfigureAwait(false);
        if (link.UnlinkedAt is null)
        {
            link.UnlinkedAt = Now; link.UnlinkReason = reason;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ReviewAsync(Guid profileId, string actor, string reason, CancellationToken cancellationToken) =>
        ModerateAsync(profileId, ProfileState.Reviewed, actor, reason, cancellationToken);

    public Task RejectAsync(Guid profileId, string actor, string reason, CancellationToken cancellationToken) =>
        ModerateAsync(profileId, ProfileState.Rejected, actor, reason, cancellationToken);

    private async Task ModerateAsync(Guid profileId, ProfileState state, string actor, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync("profile:" + profileId, ct).ConfigureAwait(false);
        var profile = await LockProfileAsync(profileId, ct).ConfigureAwait(false);
        if (profile.State == ProfileState.Archived ||
            await db.Set<OriginalPost>().AnyAsync(x => x.Id == profile.PostId && x.Excluded, ct).ConfigureAwait(false) ||
            (state == ProfileState.Reviewed && profile.State is not (ProfileState.Candidate or ProfileState.Rejected)))
            throw new InvalidOperationException("Переход состояния анкеты недопустим.");
        if (profile.State != state)
        {
            profile.State = state;
            db.Add(new ModerationDecision { ProfileId = profileId, Actor = actor, Action = state == ProfileState.Reviewed ? "review" : "reject", Reason = reason, CreatedAt = Now });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task PublishAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync("profile:" + profileId, cancellationToken).ConfigureAwait(false);
        var profile = await LockProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile.State == ProfileState.Rejected)
            throw new InvalidOperationException("Отклонённая анкета требует явной повторной проверки перед публикацией.");
        var revisionId = await db.Set<Analysis>().Where(a => a.Id == profile.AnalysisId)
            .Select(a => a.RevisionId).SingleAsync(cancellationToken).ConfigureAwait(false);
        await RequireCurrentIngestedRevisionAsync(profile.PostId, revisionId, cancellationToken).ConfigureAwait(false);
        profile.State = ProfileState.Published;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ArchiveAsync(Guid profileId, string actor, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var profile = await db.Set<Profile>().SingleAsync(x => x.Id == profileId, cancellationToken).ConfigureAwait(false);
        await db.Set<OriginalPost>().Where(x => x.Id == profile.PostId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Excluded, true), cancellationToken).ConfigureAwait(false);
        await db.Set<Profile>().Where(x => x.PostId == profile.PostId).ExecuteUpdateAsync(x => x.SetProperty(p => p.State, ProfileState.Archived), cancellationToken).ConfigureAwait(false);
        db.Add(new ModerationDecision { ProfileId = profileId, Actor = actor, Action = "archive", Reason = reason, CreatedAt = Now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public Task<PublicProfile?> LatestAsync(Guid anonId, CancellationToken cancellationToken) =>
        (from profile in db.Set<Profile>()
         join link in db.Set<ProfileLink>() on profile.Id equals link.ProfileId
         join post in db.Set<OriginalPost>() on profile.PostId equals post.Id
         join analysis in db.Set<Analysis>() on profile.AnalysisId equals analysis.Id
         where link.AnonId == anonId && link.UnlinkedAt == null && profile.State == ProfileState.Published && !post.Excluded
         orderby profile.CreatedAt descending, profile.Id descending
         select new PublicProfile(profile.Id, analysis.Age, analysis.Location, analysis.GenderDescription, analysis.Interests, analysis.Goals))
        .FirstOrDefaultAsync(cancellationToken);

    public async Task<Guid> RegisterBotUserAsync(long technicalId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(technicalId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync("bot:" + technicalId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        var user = await db.Set<BotUser>().SingleOrDefaultAsync(x => x.TechnicalId == technicalId, cancellationToken).ConfigureAwait(false);
        if (user is null) { user = new BotUser { TechnicalId = technicalId }; db.Add(user); await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return user.Id;
    }

    public async Task BlacklistAsync(Guid ownerId, Guid anonId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO product."BlacklistEntry" ("Id","OwnerId","AnonId","Reason","AddedAt")
            VALUES ({Guid.NewGuid()},{ownerId},{anonId},{reason},{Now})
            ON CONFLICT ("OwnerId","AnonId") DO UPDATE SET "Reason"=EXCLUDED."Reason"
            """, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> ComplainAsync(Guid ownerId, Guid profileId, Guid? contactId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var complaint = new Complaint { OwnerId = ownerId, ProfileId = profileId, ContactId = contactId, Reason = reason, CreatedAt = Now };
        db.Add(complaint);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return complaint.Id;
    }

    public async Task<IReadOnlyList<PersonalBlacklistItem>> ReadBlacklistAsync(Guid ownerId, CancellationToken cancellationToken) =>
        await db.Set<BlacklistEntry>().Where(x => x.OwnerId == ownerId).OrderBy(x => x.AddedAt)
            .Select(x => new PersonalBlacklistItem(x.Id, x.AnonId, x.Reason, x.AddedAt)).ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async Task SaveSettingsAsync(Guid ownerId, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var user = await db.Set<BotUser>().SingleAsync(x => x.Id == ownerId, cancellationToken).ConfigureAwait(false);
        await db.Entry(user).ReloadAsync(cancellationToken).ConfigureAwait(false);
        user.Settings = JsonSerializer.Serialize(settings);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadSettingsAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var json = await db.Set<BotUser>().Where(x => x.Id == ownerId).Select(x => x.Settings).SingleAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    }

    public async Task RecordOwnershipCheckAsync(Guid complaintId, OwnershipCheckResult result, string actor, string evidence, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        if (!Enum.IsDefined(result)) throw new ArgumentOutOfRangeException(nameof(result));
        var complaint = await db.Set<Complaint>().SingleAsync(x => x.Id == complaintId, cancellationToken).ConfigureAwait(false);
        db.Add(new ModerationDecision
        {
            ComplaintId = complaintId,
            ProfileId = complaint.ProfileId,
            Actor = actor,
            Action = "ownership_" + result.ToString().ToLowerInvariant(),
            Reason = "Проверка владения контактом",
            OwnershipEvidence = evidence,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> EnqueueAsync(Guid revisionId, string processorVersion, int maxAttempts, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorVersion);
        // Блокировка поста отдельным оператором: проверка причины ревизии в следующем
        // операторе увидит изменения, зафиксированные до получения этой блокировки.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM product."OriginalPost" AS post
            JOIN product."PostRevision" AS revision ON revision."PostId"=post."Id"
            WHERE revision."Id"={revisionId}
            FOR UPDATE OF post
            """, cancellationToken).ConfigureAwait(false);
        var ids = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO product."ProcessingJob" ("Id","RevisionId","ProcessorVersion","MaxAttempts","Attempts","State","AvailableAt")
            SELECT {Guid.NewGuid()},revision."Id",{processorVersion},{maxAttempts},0,'pending',{Now}
            FROM product."PostRevision" AS revision
            JOIN product."OriginalPost" AS post ON post."Id"=revision."PostId"
            WHERE revision."Id"={revisionId} AND NOT post."Excluded"
            AND (post."IngestedRevisionId" IS NULL OR
                (post."IngestedRevisionId"=revision."Id" AND revision."IngestionReason"='candidate'))
            ON CONFLICT ("RevisionId","ProcessorVersion") DO UPDATE SET "ProcessorVersion"=EXCLUDED."ProcessorVersion"
            RETURNING "Id" AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Count == 1 ? ids[0] : throw new InvalidOperationException("Ревизия недоступна для постановки в очередь.");
    }

    public async Task<JobLease?> ClaimAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(lease));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Закрываем просроченные попытки вместе со снятием аренды. При возврате
        // ревизии незавершённое задание может продолжиться с оставшимися попытками.
        var expirationCutoff = Now;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS MATERIALIZED (
                SELECT "Id" FROM product."ProcessingJob"
                WHERE "State"='running' AND "LeaseUntil"<={expirationCutoff}
                FOR UPDATE SKIP LOCKED
            ), released AS (
                UPDATE product."ProcessingJob" AS job
                SET "State"=CASE WHEN job."Attempts">=job."MaxAttempts" THEN 'failed' ELSE 'pending' END,
                    "LeaseToken"=NULL,"LeaseUntil"=NULL,"FailureCategory"='lease_expired'
                FROM expired WHERE job."Id"=expired."Id"
                RETURNING job."Id")
            UPDATE product."JobAttempt" SET "FinishedAt"={expirationCutoff},"FailureCategory"='lease_expired'
            WHERE "FinishedAt" IS NULL AND "JobId" IN (SELECT "Id" FROM released)
            """, cancellationToken).ConfigureAwait(false);
        var jobs = await db.Database.SqlQuery<Guid>($"""
            SELECT job."Id" AS "Value"
            FROM product."ProcessingJob" AS job
            JOIN product."PostRevision" AS revision ON revision."Id"=job."RevisionId"
            JOIN product."OriginalPost" AS post ON post."Id"=revision."PostId"
            WHERE job."State"='pending'
            AND job."Attempts"<job."MaxAttempts" AND job."AvailableAt"<={Now}
            AND NOT post."Excluded"
            AND (post."IngestedRevisionId" IS NULL OR
                (post."IngestedRevisionId"=revision."Id" AND revision."IngestionReason"='candidate'))
            ORDER BY job."AvailableAt", job."Id" LIMIT {ClaimCandidates}
            FOR UPDATE OF post SKIP LOCKED
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        // Блокировка поста взята первой и в том же порядке, что в EnqueueAsync и ArchiveAsync.
        // Решение о доступности поста и ревизии остаётся в силе до конца захвата, поэтому
        // исключённый пост не получает аренду даже при одновременной архивации.
        ProcessingJob? job = null;
        foreach (var candidate in jobs)
        {
            // Задание перечитывается под собственной блокировкой: аренда выдаётся только
            // заданию, которое всё ещё ожидает и не исчерпало попытки, а пост и ревизия
            // проверяются заново и видят зафиксированные изменения.
            var locked = await db.Set<ProcessingJob>().FromSqlInterpolated($"""
                SELECT job.* FROM product."ProcessingJob" AS job
                JOIN product."PostRevision" AS revision ON revision."Id"=job."RevisionId"
                JOIN product."OriginalPost" AS post ON post."Id"=revision."PostId"
                WHERE job."Id"={candidate} AND job."State"='pending'
                AND job."Attempts"<job."MaxAttempts" AND job."AvailableAt"<={Now}
                AND NOT post."Excluded"
                AND (post."IngestedRevisionId" IS NULL OR
                    (post."IngestedRevisionId"=revision."Id" AND revision."IngestionReason"='candidate'))
                FOR UPDATE OF job
                """).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (locked.Count == 0)
                continue;
            job = locked[0];
            break;
        }
        if (job is null) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return null; }
        await db.Entry(job).ReloadAsync(cancellationToken).ConfigureAwait(false);
        job.Attempts++; job.State = "running"; job.LeaseToken = Guid.NewGuid(); job.LeaseUntil = Now + lease;
        db.Add(new JobAttempt { JobId = job.Id, Number = job.Attempts, Token = job.LeaseToken.Value, StartedAt = Now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new JobLease(job.Id, job.LeaseToken.Value, job.RevisionId, job.Attempts, job.LeaseUntil.Value);
    }

    public async Task CompleteAsync(JobLease lease, string? failureCategory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (failureCategory is { Length: > 100 }) throw new ArgumentException("Категория ошибки слишком длинная.", nameof(failureCategory));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await db.Set<ProcessingJob>().FromSqlInterpolated($"""SELECT * FROM product."ProcessingJob" WHERE "Id"={lease.JobId} FOR UPDATE""").ToListAsync(cancellationToken).ConfigureAwait(false);
        var job = jobs.Single();
        await db.Entry(job).ReloadAsync(cancellationToken).ConfigureAwait(false);
        if (job.LeaseToken != lease.Token || job.LeaseUntil <= Now || job.State != "running") throw new InvalidOperationException("Захват задания истёк или заменён.");
        job.State = failureCategory is null ? "succeeded" : job.Attempts < job.MaxAttempts ? "pending" : "failed";
        job.FailureCategory = failureCategory; job.LeaseUntil = null; job.LeaseToken = null;
        job.AvailableAt = Now + TimeSpan.FromSeconds(Math.Min(300, job.Attempts * 10));
        var attempt = await db.Set<JobAttempt>().SingleAsync(x => x.Token == lease.Token, cancellationToken).ConfigureAwait(false);
        attempt.FinishedAt = Now; attempt.FailureCategory = failureCategory;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}