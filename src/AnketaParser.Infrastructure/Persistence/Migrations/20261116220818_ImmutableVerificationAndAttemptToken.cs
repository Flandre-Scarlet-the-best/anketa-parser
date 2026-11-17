using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnketaParser.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImmutableVerificationAndAttemptToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            // Подтверждение хранит историческое основание: смена контакта/аккаунта требует новой записи.
            migrationBuilder.Sql("""
                CREATE FUNCTION product.guard_verification_identity() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."ContactId" IS DISTINCT FROM OLD."ContactId" OR NEW."AccountId" IS DISTINCT FROM OLD."AccountId" THEN
                    RAISE EXCEPTION 'verification_identity_immutable' USING ERRCODE='23514';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER guard_verification_identity BEFORE UPDATE ON product."ContactVerification"
                  FOR EACH ROW EXECUTE FUNCTION product.guard_verification_identity();
                """);
            migrationBuilder.CreateIndex(
                name: "IX_JobAttempt_Token",
                schema: "product",
                table: "JobAttempt",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.Sql("""
                DROP TRIGGER guard_verification_identity ON product."ContactVerification";
                DROP FUNCTION product.guard_verification_identity();
                """);
            migrationBuilder.DropIndex(
                name: "IX_JobAttempt_Token",
                schema: "product",
                table: "JobAttempt");
        }
    }
}