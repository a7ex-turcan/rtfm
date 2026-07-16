using Rtfm.Core.Database;

namespace Rtfm.Core.Tests.Database;

/// <summary>
/// Descriptor query-block logic + result rendering (Phase 23). Live read-only
/// enforcement and truncation are exercised against a throwaway database at phase
/// time, not here (no network in unit tests — same boundary as Phase 20).
/// </summary>
public class DatabaseQueryTests
{
    [Fact]
    public void Descriptor_without_query_block_is_not_queryable()
    {
        var descriptor = DbDescriptor.Parse("""{ "provider": "postgres", "connectionString": "Host=x" }""");
        Assert.False(descriptor.IsQueryable);
        Assert.Throws<InvalidOperationException>(() => descriptor.ResolveQuery());
    }

    [Fact]
    public void Query_block_makes_descriptor_queryable_with_its_own_connection_and_caps()
    {
        Environment.SetEnvironmentVariable("RTFM_TEST_RO", "Host=ro;Database=d");
        try
        {
            var descriptor = DbDescriptor.Parse(
                """
                {
                  "provider": "postgres",
                  "connectionString": "Host=rw",
                  "query": { "connectionString": "${RTFM_TEST_RO}", "maxRows": 50, "timeoutSeconds": 3 }
                }
                """);

            Assert.True(descriptor.IsQueryable);
            var q = descriptor.ResolveQuery();
            Assert.Equal("Host=ro;Database=d", q.ConnectionString);
            Assert.Equal(50, q.MaxRows);
            Assert.Equal(3, q.TimeoutSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTFM_TEST_RO", null);
        }
    }

    [Fact]
    public void Query_block_without_connection_falls_back_to_schema_pull_string()
    {
        var descriptor = DbDescriptor.Parse(
            """{ "provider": "postgres", "connectionString": "Host=shared", "query": { } }""");

        var q = descriptor.ResolveQuery();
        Assert.Equal("Host=shared", q.ConnectionString);
        // Unspecified caps take the defaults.
        Assert.Equal(DbQueryBlock.DefaultMaxRows, q.MaxRows);
        Assert.Equal(DbQueryBlock.DefaultTimeoutSeconds, q.TimeoutSeconds);
    }

    [Fact]
    public void Reads_are_the_default_until_a_descriptor_opts_into_writes()
    {
        var readOnly = DbDescriptor.Parse(
            """{ "provider": "postgres", "connectionString": "Host=x", "query": { } }""");
        Assert.False(readOnly.AllowsWrites);
        Assert.False(readOnly.ResolveQuery().AllowWrites);

        var writable = DbDescriptor.Parse(
            """{ "provider": "postgres", "connectionString": "Host=x", "query": { "allowWrites": true } }""");
        Assert.True(writable.AllowsWrites);
        Assert.True(writable.ResolveQuery().AllowWrites);
    }

    [Fact]
    public void A_descriptor_with_no_query_block_is_never_writable()
    {
        var descriptor = DbDescriptor.Parse("""{ "provider": "postgres", "connectionString": "Host=x" }""");
        Assert.False(descriptor.AllowsWrites);
    }

    [Fact]
    public void Markdown_table_renders_headers_rows_and_escapes_pipes()
    {
        var result = DbQueryResult.Ok(
            columns: ["id", "name"],
            rows: [["1", "a|b"], ["2", null]],
            truncated: false);

        var md = result.ToMarkdownTable();
        var lines = md.Split('\n');

        Assert.Equal("| id | name |", lines[0]);
        Assert.Equal("| --- | --- |", lines[1]);
        Assert.Equal(@"| 1 | a\|b |", lines[2]);   // pipe inside a cell is escaped
        Assert.Equal("| 2 |  |", lines[3]);        // null renders as empty
        Assert.Equal(2, result.RowCount);
    }

    [Fact]
    public void Refused_and_failed_results_carry_the_reason_and_no_rows()
    {
        var refused = DbQueryResult.Refused("schema only");
        Assert.False(refused.Success);
        Assert.Equal("schema only", refused.Error);
        Assert.Empty(refused.Rows);
    }
}
