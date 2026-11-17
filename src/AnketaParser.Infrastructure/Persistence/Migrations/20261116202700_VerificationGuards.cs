using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AnketaParser.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ProductDbContext))]
[Migration("20261116202700_VerificationGuards")]
public sealed class VerificationGuards : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            ALTER TABLE product."ProfileLink" ADD CONSTRAINT link_verification_required
              CHECK (("Confidence"=1)=("VerificationId" IS NOT NULL));
            ALTER TABLE private."TelegramAccount" ADD CONSTRAINT account_id_positive CHECK ("TechnicalId">0);
            ALTER TABLE private."BotUser" ADD CONSTRAINT bot_id_positive CHECK ("TechnicalId">0);
            CREATE FUNCTION product.guard_verified_link() RETURNS trigger LANGUAGE plpgsql AS $$
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
            CREATE TRIGGER guard_verified_link BEFORE INSERT OR UPDATE ON product."ProfileLink"
              FOR EACH ROW EXECUTE FUNCTION product.guard_verified_link();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            DROP TRIGGER guard_verified_link ON product."ProfileLink";
            DROP FUNCTION product.guard_verified_link();
            ALTER TABLE product."ProfileLink" DROP CONSTRAINT link_verification_required;
            ALTER TABLE private."TelegramAccount" DROP CONSTRAINT account_id_positive;
            ALTER TABLE private."BotUser" DROP CONSTRAINT bot_id_positive;
            """);
    }
}