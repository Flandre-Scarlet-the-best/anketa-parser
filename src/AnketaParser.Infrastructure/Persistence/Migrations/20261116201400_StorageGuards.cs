using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AnketaParser.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ProductDbContext))]
[Migration("20261116201400_StorageGuards")]
public sealed class StorageGuards : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE FUNCTION product.guard_profile() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              PERFORM 1 FROM product."OriginalPost" WHERE "Id"=NEW."PostId" FOR UPDATE;
              IF NOT EXISTS (
                SELECT 1 FROM product."Analysis" a JOIN product."PostRevision" r ON r."Id"=a."RevisionId"
                WHERE a."Id"=NEW."AnalysisId" AND r."PostId"=NEW."PostId")
              THEN RAISE EXCEPTION 'profile_origin_mismatch' USING ERRCODE='23514'; END IF;
              IF NEW."State"=2 AND EXISTS(SELECT 1 FROM product."OriginalPost" WHERE "Id"=NEW."PostId" AND "Excluded")
              THEN RAISE EXCEPTION 'post_excluded' USING ERRCODE='23514'; END IF;
              IF TG_OP='UPDATE' AND OLD."State"=4 AND NEW."State"<>4
              THEN RAISE EXCEPTION 'archive_is_terminal' USING ERRCODE='23514'; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER guard_profile BEFORE INSERT OR UPDATE ON product."Profile"
              FOR EACH ROW EXECUTE FUNCTION product.guard_profile();

            CREATE FUNCTION product.guard_post() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF OLD."Excluded" AND NOT NEW."Excluded"
              THEN RAISE EXCEPTION 'exclusion_is_terminal' USING ERRCODE='23514'; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER guard_post BEFORE UPDATE ON product."OriginalPost"
              FOR EACH ROW EXECUTE FUNCTION product.guard_post();

            CREATE FUNCTION product.guard_link() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NOT EXISTS (
                SELECT 1 FROM product."Profile" p
                JOIN product."Analysis" a ON a."Id"=p."AnalysisId"
                JOIN product."ContactObservation" o ON o."RevisionId"=a."RevisionId"
                WHERE p."Id"=NEW."ProfileId" AND o."ContactId"=NEW."ContactId")
              THEN RAISE EXCEPTION 'contact_origin_mismatch' USING ERRCODE='23514'; END IF;
              IF NEW."Confidence"=1 AND NOT EXISTS (
                SELECT 1 FROM product."ContactVerification" WHERE "ContactId"=NEW."ContactId")
              THEN RAISE EXCEPTION 'account_not_verified' USING ERRCODE='23514'; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER guard_link BEFORE INSERT OR UPDATE ON product."ProfileLink"
              FOR EACH ROW EXECUTE FUNCTION product.guard_link();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            DROP TRIGGER guard_link ON product."ProfileLink";
            DROP FUNCTION product.guard_link();
            DROP TRIGGER guard_post ON product."OriginalPost";
            DROP FUNCTION product.guard_post();
            DROP TRIGGER guard_profile ON product."Profile";
            DROP FUNCTION product.guard_profile();
            """);
    }
}