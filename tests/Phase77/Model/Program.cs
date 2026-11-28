using ImagingAI.Api.Data;
using ImagingAI.Api.Models.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
static void Check(bool pass, string item) { if (!pass) throw new Exception(item); Console.WriteLine("PASS " + item); }
using var db = new ImagingAiDbContext(new DbContextOptionsBuilder<ImagingAiDbContext>()
    .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=offline").Options);
var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(UserAccount))!;
var email = entity.FindProperty(nameof(UserAccount.Email))!;
var verified = entity.FindProperty(nameof(UserAccount.EmailVerifiedAt))!;
Check(email.IsNullable && email.GetColumnName() == "email" && email.GetColumnType() == "character varying(254)" && email.GetMaxLength() == 254, "nullable varchar(254) email mapping");
Check(verified.ClrType == typeof(DateTime?) && verified.IsNullable && verified.GetColumnName() == "email_verified_at" && verified.GetColumnType() == "timestamp with time zone", "nullable timestamptz mapping");
Check(entity.GetCheckConstraints().Any(c => c.Name == "ck_user_account_email_nonblank" && c.Sql == "email IS NULL OR (length(email) > 0 AND email !~ '[[:space:][:cntrl:]]')") &&
entity.GetCheckConstraints().Any(c => c.Name == "ck_user_account_email_verified_requires_email" && c.Sql == "email_verified_at IS NULL OR email IS NOT NULL") &&
entity.GetCheckConstraints().Any(c => c.Name == "ck_user_account_distinct_roles"), "three CHECK mappings");
Check(entity.FindPrimaryKey()!.Properties.Single().Name == "UserId" &&
entity.GetForeignKeys().Select(f => f.GetConstraintName()).Order().SequenceEqual(new[]{"fk_user_role","fk_user_secondary_role"}), "original PK and role relationships");
Check(!entity.GetIndexes().Any(i => i.Properties.Contains(email)), "no ordinary Email index");
var query = db.UserAccounts.Include(x => x.Role).Include(x => x.SecondaryRole).ToQueryString();
Check(query.Contains("email_verified_at") && query.Contains("LEFT JOIN"), "full entity projection and optional role join");
Check(db.Database.GetDbConnection().State == System.Data.ConnectionState.Closed, "connection never opened");
