using System.Text;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

public class SqlConverterTests
{
    /// <summary>A realistic Postgres-flavored dump: inline + ALTER FKs, comments, enum, view, index, seed data.</summary>
    private const string SchemaDump =
        """
        -- Core schema for the platform
        CREATE TYPE user_role AS ENUM ('admin', 'super-admin', 'member');

        CREATE TABLE public.accounts (
            account_id   uuid PRIMARY KEY,
            tenant_name  text NOT NULL,
            created_at   timestamptz NOT NULL DEFAULT now()
        );

        COMMENT ON TABLE public.accounts IS 'One row per customer organisation.';
        COMMENT ON COLUMN public.accounts.tenant_name IS 'Display name shown in the admin portal.';

        CREATE TABLE users (
            user_id    uuid PRIMARY KEY,
            account_id uuid NOT NULL REFERENCES accounts(account_id),
            role       user_role NOT NULL DEFAULT 'member',
            email      varchar(320) UNIQUE
        );

        CREATE TABLE subscriptions (
            subscription_id uuid,
            account_id      uuid NOT NULL,
            plan            text,
            PRIMARY KEY (subscription_id)
        );

        ALTER TABLE ONLY subscriptions
            ADD CONSTRAINT fk_sub_account FOREIGN KEY (account_id) REFERENCES public.accounts(account_id);

        CREATE UNIQUE INDEX idx_users_email ON users (email);

        CREATE VIEW active_accounts AS
            SELECT account_id, tenant_name FROM accounts WHERE created_at > now() - interval '90 days';

        CREATE FUNCTION touch() RETURNS trigger AS $body$
            BEGIN NEW.updated_at := now(); RETURN NEW; END; -- has a ; inside
        $body$ LANGUAGE plpgsql;

        INSERT INTO accounts VALUES ('a', 'Acme', now());
        INSERT INTO users VALUES ('u', 'a', 'admin', 'x@acme.test');
        """;

    [Fact]
    public void Tables_render_as_sections_with_columns_flags_and_comments()
    {
        var result = Convert(SchemaDump, "core-schema.sql");

        Assert.Equal(SourceFormat.Sql, result.Format);
        Assert.Contains("## Table: public.accounts", result.Markdown);
        Assert.Contains("One row per customer organisation.", result.Markdown);
        Assert.Contains("- account_id uuid — PK", result.Markdown);
        Assert.Contains("- tenant_name text — NOT NULL — Display name shown in the admin portal.", result.Markdown);
        Assert.Contains("DEFAULT now()", result.Markdown);
        Assert.Contains("- email varchar(320) — UNIQUE", result.Markdown);
        // PK declared via table-level constraint still marks the column.
        Assert.Contains("- subscription_id uuid — PK", result.Markdown);
    }

    [Fact]
    public void Foreign_keys_appear_on_columns_in_reverse_index_and_in_relationships()
    {
        var markdown = Convert(SchemaDump, "s.sql").Markdown;

        Assert.Contains("- account_id uuid — NOT NULL, FK → accounts(account_id)", markdown); // inline FK
        Assert.Contains("Referenced by: users (account_id), subscriptions (account_id)", markdown); // computed reverse
        Assert.Contains("## Relationships", markdown);
        Assert.Contains("- subscriptions (account_id) → public.accounts (account_id)", markdown); // ALTER TABLE FK
    }

    [Fact]
    public void Secondary_objects_and_noise_are_summarized()
    {
        var markdown = Convert(SchemaDump, "s.sql").Markdown;

        Assert.Contains("TYPE user_role AS ENUM (admin, super-admin, member)", markdown);
        Assert.Contains("INDEX idx_users_email on users (email)", markdown);
        Assert.Contains("VIEW active_accounts AS SELECT account_id", markdown);
        Assert.Contains("FUNCTION touch", markdown); // dollar-quoted body didn't break statement splitting
        Assert.Contains("2 data statements", markdown);
    }

    [Fact]
    public void Unparseable_sql_falls_back_to_a_fenced_block()
    {
        var result = Convert("SELECT weird_dialect_stuff FROM somewhere WHERE x", "adhoc.sql");

        Assert.Contains("```sql", result.Markdown);
        Assert.Contains("weird_dialect_stuff", result.Markdown);
    }

    [Fact]
    public void Detector_routes_sql_by_extension()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("CREATE TABLE t (id int);"));
        Assert.Equal(SourceFormat.Sql, FormatDetector.Detect("schema.sql", stream));
    }

    [Theory]
    [InlineData("public.accounts", "accounts", true)]
    [InlineData("\"Public\".\"Accounts\"", "accounts", false)] // CleanIdentifier strips quotes before compare
    [InlineData("users", "accounts", false)]
    public void Table_name_matching_ignores_schema_qualification(string a, string b, bool equal)
        => Assert.Equal(equal, SqlSchemaRenderer.TableNamesEqual(a, b));

    [Fact]
    public void Quoted_identifier_matching_works_after_cleaning()
        => Assert.True(SqlSchemaRenderer.TableNamesEqual(SqlConverter.CleanIdentifier("\"Public\".\"Accounts\""), "ACCOUNTS"));

    private static ConversionResult Convert(string sql, string path)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sql));
        return new SqlConverter().Convert(stream, path);
    }
}
