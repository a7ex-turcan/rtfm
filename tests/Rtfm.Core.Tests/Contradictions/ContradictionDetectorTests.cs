using System.Text.Json;
using Rtfm.Core.Chunking;
using Rtfm.Core.Contradictions;

namespace Rtfm.Core.Tests.Contradictions;

public class ContradictionDetectorTests
{
    private static readonly DateTimeOffset Older = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pair_id_is_deterministic_and_side_order_independent()
    {
        var a = new ContradictionSide("d:/docs/new.md", 2, "H", Newer, "x");
        var b = new ContradictionSide("d:/docs/old.md", 5, "H", Older, "y");

        var pair1 = new ContradictionPair("p", a, b, 0.9, DateTimeOffset.UnixEpoch);
        var pair2 = new ContradictionPair("p", b, a, 0.9, DateTimeOffset.UnixEpoch); // sides swapped

        Assert.Equal(pair1.Id, pair2.Id);
        Assert.Equal(16, pair1.Id.Length);
    }

    [Theory]
    [InlineData("The default role is admin.", "The default role is super-admin.", true)]   // the §2.13 case
    [InlineData("The default role is admin.", "The default role is ADMIN.", false)]        // case-only → copy
    [InlineData("Same  text\nhere.", "same text here.", false)]                            // whitespace-only → copy
    public void ShouldNominate_requires_texts_that_actually_differ(string a, string b, bool expected)
        => Assert.Equal(expected, ContradictionDetector.ShouldNominate(a, Newer, b, Older));

    [Fact]
    public void ShouldNominate_requires_different_dates()
    {
        Assert.False(ContradictionDetector.ShouldNominate("admin", Newer, "super-admin", Newer)); // same date
        Assert.False(ContradictionDetector.ShouldNominate("admin", null, "super-admin", Older));  // unknown date
        Assert.True(ContradictionDetector.ShouldNominate("admin", Newer, "super-admin", Older));
    }

    [Fact]
    public void EvaluateCandidates_nominates_the_best_qualifying_hit_with_newer_side_first()
    {
        var chunk = new Chunk(0, "d:/docs/new.md", "Security > Roles", "The default role is super-admin.", "New", Newer, "pam");

        var response = JsonSerializer.Serialize(new
        {
            hits = new
            {
                hits = new object[]
                {
                    new // near-identical copy — skipped by the text filter
                    {
                        _score = 0.99,
                        _source = new
                        {
                            source_path = "d:/docs/copy.md", ordinal = 1, heading_path = "Security > Roles",
                            content = "Security > Roles\n\nThe default role is  SUPER-ADMIN.",
                            source_modified_at = "2026-06-05T00:00:00Z",
                        },
                    },
                    new // the real disagreement
                    {
                        _score = 0.90,
                        _source = new
                        {
                            source_path = "d:/docs/old.md", ordinal = 3, heading_path = "Security > Roles",
                            content = "Security > Roles\n\nThe default role is admin.",
                            source_modified_at = "2026-06-01T00:00:00Z",
                        },
                    },
                    new // below the similarity floor — never reached
                    {
                        _score = 0.50,
                        _source = new
                        {
                            source_path = "d:/docs/other.md", ordinal = 0, heading_path = "X",
                            content = "X\n\nUnrelated.", source_modified_at = "2026-01-01T00:00:00Z",
                        },
                    },
                },
            },
        });

        var pair = ContradictionDetector.EvaluateCandidates(chunk, response);

        Assert.NotNull(pair);
        Assert.Equal("pam", pair.Project);
        Assert.Equal("d:/docs/new.md", pair.A.Path); // newer side is A
        Assert.Equal("d:/docs/old.md", pair.B.Path);
        Assert.Equal(0.90, pair.Similarity, precision: 4);
        Assert.Contains("super-admin", pair.A.Excerpt);
        Assert.Contains("admin", pair.B.Excerpt);
    }

    [Fact]
    public void EvaluateCandidates_returns_null_when_nothing_qualifies()
    {
        var chunk = new Chunk(0, "d:/docs/a.md", "H", "text", null, Newer, "pam");
        var response = """{"hits":{"hits":[{"_score":0.6,"_source":{"source_path":"d:/docs/b.md","ordinal":0,"heading_path":"H","content":"H\n\nother","source_modified_at":"2026-06-01T00:00:00Z"}}]}}""";

        Assert.Null(ContradictionDetector.EvaluateCandidates(chunk, response));
    }

    [Fact]
    public void Candidate_query_scopes_project_and_excludes_self()
    {
        using var doc = JsonDocument.Parse(ContradictionDetector.BuildCandidateQuery([0.1f], "d:/docs/self.md", "pam"));
        var knn = doc.RootElement.GetProperty("query").GetProperty("knn").GetProperty("content_vector");
        var filter = knn.GetProperty("filter").GetProperty("bool");

        Assert.Equal("pam", filter.GetProperty("filter")[0].GetProperty("term").GetProperty("project").GetString());
        Assert.Equal("d:/docs/self.md", filter.GetProperty("must_not")[0].GetProperty("term").GetProperty("source_path").GetString());
    }

    [Fact]
    public void Bulk_payload_uses_deterministic_pair_ids()
    {
        var pair = new ContradictionPair(
            "pam",
            new ContradictionSide("d:/docs/new.md", 1, "H", Newer, "new text"),
            new ContradictionSide("d:/docs/old.md", 2, "H", Older, "old text"),
            0.9,
            DateTimeOffset.UnixEpoch);

        var payload = ContradictionDetector.BuildBulkPayload([pair], Newer);
        var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Contains($"\"_id\":\"{pair.Id}\"", lines[0]);

        using var docLine = JsonDocument.Parse(lines[1]);
        Assert.Equal("pam", docLine.RootElement.GetProperty("project").GetString());
        Assert.Equal("d:/docs/new.md", docLine.RootElement.GetProperty("a_path").GetString());
        Assert.True(docLine.RootElement.TryGetProperty("detected_at", out _));
    }
}
