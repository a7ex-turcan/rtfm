using Rtfm.Core.Generated;

namespace Rtfm.Core.Tests.Generated;

public class GeneratedDocumentStoreTests
{
    [Theory]
    [InlineData("Feature analysis: MSP tenant switching", "feature-analysis-msp-tenant-switching")]
    [InlineData("  What's *missing* in RBAC??  ", "what-s-missing-in-rbac")]
    [InlineData("ALL CAPS TITLE", "all-caps-title")]
    [InlineData("!!!", "")]
    public void Slugify_produces_stable_filesystem_safe_names(string title, string expected)
        => Assert.Equal(expected, GeneratedDocumentStore.Slugify(title));

    [Fact]
    public void Slugify_caps_length()
        => Assert.True(GeneratedDocumentStore.Slugify(new string('a', 500)).Length <= 80);

    [Fact]
    public void File_content_leads_with_title_and_provenance()
    {
        var content = GeneratedDocumentStore.BuildFileContent(
            "Gap analysis", "## Missing bits\n\nNo audit trail.", "alex", new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero));

        var lines = content.Split('\n');
        Assert.Equal("# Gap analysis", lines[0]);
        Assert.Contains("LLM-assisted document, saved by alex on 2026-07-03", content);
        Assert.Contains("## Missing bits", content);
        Assert.Contains("No audit trail.", content);
    }

    [Fact]
    public void Duplicate_leading_h1_is_not_doubled()
    {
        var content = GeneratedDocumentStore.BuildFileContent(
            "Gap analysis", "# Gap Analysis\n\nBody text.", "alex", DateTimeOffset.UnixEpoch);

        Assert.Single(content.Split('\n'), l => l.StartsWith("# ", StringComparison.Ordinal));
        Assert.Contains("Body text.", content);
    }
}
