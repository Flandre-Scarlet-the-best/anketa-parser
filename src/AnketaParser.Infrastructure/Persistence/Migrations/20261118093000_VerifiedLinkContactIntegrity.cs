using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AnketaParser.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ProductDbContext))]
[Migration("20261118093000_VerifiedLinkContactIntegrity")]
public sealed class VerifiedLinkContactIntegrity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        // Соответствие подтверждения контакту проверяется и у отменённых связей: ранний возврат по
        // UnlinkedAt позволял оставить связь с подтверждением другого контакта после смены ContactId.
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION product.guard_verified_link() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE account_id uuid;
            BEGIN
              IF NEW."Confidence"<>1 THEN RETURN NEW; END IF;
              SELECT "AccountId" INTO account_id FROM product."ContactVerification"
                WHERE "Id"=NEW."VerificationId" AND "ContactId"=NEW."ContactId";
              IF account_id IS NULL THEN
                RAISE EXCEPTION 'verification_contact_mismatch' USING ERRCODE='23514';
              END IF;
              IF NEW."UnlinkedAt" IS NOT NULL THEN RETURN NEW; END IF;
              PERFORM 1 FROM product."Anon" WHERE "Id"=NEW."AnonId" FOR UPDATE;
              IF EXISTS (
                SELECT 1 FROM product."ProfileLink" l
                JOIN product."ContactVerification" v ON v."Id"=l."VerificationId"
                WHERE l."AnonId"=NEW."AnonId" AND l."Id"<>NEW."Id" AND l."UnlinkedAt" IS NULL
                  AND l."Confidence"=1 AND v."AccountId"<>account_id)
              THEN RAISE EXCEPTION 'different_verified_accounts' USING ERRCODE='23514'; END IF;
              RETURN NEW;
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION product.guard_verified_link() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE account_id uuid;
            BEGIN
              IF NEW."Confidence"<>1 OR NEW."UnlinkedAt" IS NOT NULL THEN RETURN NEW; END IF;
              PERFORM 1 FROM product."Anon" WHERE "Id"=NEW."AnonId" FOR UPDATE;
              SELECT "AccountId" INTO account_id FROM product."ContactVerification"
                WHERE "Id"=NEW."VerificationId" AND "ContactId"=NEW."ContactId";
              IF account_id IS NULL THEN
                RAISE EXCEPTION 'verification_contact_mismatch' USING ERRCODE='23514';
              END IF;
              IF EXISTS (
                SELECT 1 FROM product."ProfileLink" l
                JOIN product."ContactVerification" v ON v."Id"=l."VerificationId"
                WHERE l."AnonId"=NEW."AnonId" AND l."Id"<>NEW."Id" AND l."UnlinkedAt" IS NULL
                  AND l."Confidence"=1 AND v."AccountId"<>account_id)
              THEN RAISE EXCEPTION 'different_verified_accounts' USING ERRCODE='23514'; END IF;
              RETURN NEW;
            END $$;
            """);
    }
}