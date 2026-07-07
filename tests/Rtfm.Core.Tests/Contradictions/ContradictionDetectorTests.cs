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

        var nomination = ContradictionDetector.EvaluateCandidates(chunk, response);

        Assert.NotNull(nomination);
        var pair = nomination.Pair;
        Assert.Equal("pam", pair.Project);
        Assert.Equal("d:/docs/new.md", pair.A.Path); // newer side is A
        Assert.Equal("d:/docs/old.md", pair.B.Path);
        Assert.Equal(0.90, pair.Similarity, precision: 4);
        Assert.Contains("super-admin", pair.A.Excerpt);
        Assert.Contains("admin", pair.B.Excerpt);
        // 19-day gap < 30 → peer contradiction, not supersession.
        Assert.Equal(ContradictionPair.KindContradiction, pair.Kind);
        Assert.Equal(ContradictionPair.StatusOpen, pair.Status);
        // Hashes fingerprint each side's normalized body text.
        Assert.Equal(ContradictionDetector.ContentHash("The default role is super-admin."), nomination.MineHash);
        Assert.Equal(ContradictionDetector.ContentHash("The default role is admin."), nomination.OtherHash);
    }

    [Fact]
    public void EvaluateCandidates_labels_a_large_date_gap_as_likely_supersession()
    {
        var chunk = new Chunk(0, "d:/docs/new.md", "MSP", "MSP access is governed by the msp-operator role.", "New", Newer, "pam");
        var response = JsonSerializer.Serialize(new
        {
            hits = new
            {
                hits = new object[]
                {
                    new
                    {
                        _score = 0.85,
                        _source = new
                        {
                            source_path = "d:/docs/hld.md", ordinal = 7, heading_path = "MSP",
                            content = "MSP\n\nMSP access is controlled by the msp_enabled flag.",
                            source_modified_at = "2026-01-10T00:00:00Z", // 5+ months older
                        },
                    },
                },
            },
        });

        var nomination = ContradictionDetector.EvaluateCandidates(chunk, response);

        Assert.NotNull(nomination);
        Assert.Equal(ContradictionPair.KindSupersession, nomination.Pair.Kind);
        Assert.Equal("d:/docs/new.md", nomination.Pair.A.Path); // newer still side A
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
        // Phase 22: pairs are born open with their kind recorded.
        Assert.Equal("contradiction", docLine.RootElement.GetProperty("kind").GetString());
        Assert.Equal("open", docLine.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void ContentHash_is_stable_under_case_and_whitespace_like_the_copy_check()
    {
        var hash = ContradictionDetector.ContentHash("The default role is admin.");

        Assert.Equal(hash, ContradictionDetector.ContentHash("  the DEFAULT role\nis admin. "));
        Assert.NotEqual(hash, ContradictionDetector.ContentHash("The default role is super-admin."));
        Assert.Matches("^[0-9a-f]{16}$", hash);
    }

    [Fact]
    public void Template_hashes_are_those_spanning_the_doc_threshold()
    {
        var json = JsonSerializer.Serialize(new
        {
            aggregations = new
            {
                by_hash = new
                {
                    buckets = new object[]
                    {
                        new { key = "aaaa", docs = new { value = 5 } }, // template: 5 docs share it
                        new { key = "bbbb", docs = new { value = 2 } }, // legitimate near-duplicate
                        new { key = "cccc", docs = new { value = 3 } }, // exactly at threshold
                    },
                },
            },
        });

        var hashes = ContradictionDetector.ParseTemplateHashes(json, ContradictionDetector.TemplateDocThreshold);

        Assert.Equal(new HashSet<string> { "aaaa", "cccc" }, hashes);
    }

    [Theory]
    [InlineData("content_hash")]
    [InlineData("line_hashes")]
    public void Template_count_query_scopes_project_and_targets_the_hashes(string field)
    {
        using var doc = JsonDocument.Parse(ContradictionDetector.BuildTemplateCountQuery("pam", field, ["h1", "h2"]));
        var filters = doc.RootElement.GetProperty("query").GetProperty("bool").GetProperty("filter");

        Assert.Equal("pam", filters[0].GetProperty("term").GetProperty("project").GetString());
        Assert.Equal(2, filters[1].GetProperty("terms").GetProperty(field).GetArrayLength());
        Assert.Equal(field, doc.RootElement.GetProperty("aggs").GetProperty("by_hash").GetProperty("terms").GetProperty("field").GetString());
        Assert.Equal("source_path",
            doc.RootElement.GetProperty("aggs").GetProperty("by_hash").GetProperty("aggs").GetProperty("docs").GetProperty("cardinality").GetProperty("field").GetString());
    }

    [Fact]
    public void LineHashes_skips_short_lines_and_dedupes()
    {
        var text = "| **Document Owners & Contributors** |  |\n| --- | --- |\n| **Product Manager** |  |\n| **Product Manager** |  |\nThe default role is admin, per the RBAC matrix.\n---";

        var hashes = ContradictionDetector.LineHashes(text);

        // Owners row + PM row (once) + prose line; separator rows and the duplicate dropped.
        Assert.Equal(3, hashes.Count);
        Assert.Equal(hashes, hashes.Distinct().ToList());
        // Line hashing shares the whole-chunk normalization, so a one-line
        // chunk's single line hash equals its content hash.
        Assert.Equal(
            ContradictionDetector.ContentHash("The default role is admin."),
            ContradictionDetector.LineHashes("The default role is admin.").Single());
    }

    [Fact]
    public void EvaluateCandidates_records_the_lines_both_sides_share()
    {
        var chunk = new Chunk(
            0, "d:/docs/new.md", "Roles",
            "| **Document Owners & Contributors** |  |\nThe default role is super-admin.",
            "New", Newer, "pam");

        var response = JsonSerializer.Serialize(new
        {
            hits = new
            {
                hits = new object[]
                {
                    new
                    {
                        _score = 0.90,
                        _source = new
                        {
                            source_path = "d:/docs/old.md", ordinal = 3, heading_path = "Roles",
                            content = "Roles\n\n| **Document Owners & Contributors** |  |\nThe default role is admin.",
                            source_modified_at = "2026-06-01T00:00:00Z",
                        },
                    },
                },
            },
        });

        var nomination = ContradictionDetector.EvaluateCandidates(chunk, response);

        Assert.NotNull(nomination);
        var ownersRowHash = ContradictionDetector.LineHashes("| **Document Owners & Contributors** |  |").Single();
        Assert.Equal([ownersRowHash], nomination.SharedLineHashes);
    }

    [Fact]
    public void Template_overlap_requires_majority_and_a_minimum_of_template_lines()
    {
        // 3 of 5 shared lines are template → minimum + majority met → suppressed.
        Assert.True(ContradictionDetector.IsTemplateOverlap(
            ["t1", "t2", "t3", "x1", "x2"], new HashSet<string> { "t1", "t2", "t3" }));
        // Only 2 template lines — under the minimum even though they are a majority of 3.
        Assert.False(ContradictionDetector.IsTemplateOverlap(
            ["t1", "t2", "x1"], new HashSet<string> { "t1", "t2" }));
        // 3 template lines but a minority of 8 shared — the overlap is mostly real content.
        Assert.False(ContradictionDetector.IsTemplateOverlap(
            ["t1", "t2", "t3", "x1", "x2", "x3", "x4", "x5"], new HashSet<string> { "t1", "t2", "t3" }));
        // Nothing shared verbatim (the planted admin/super-admin case) → never suppressed.
        Assert.False(ContradictionDetector.IsTemplateOverlap([], new HashSet<string> { "t1" }));
    }

    [Fact]
    public void Remove_query_spares_closed_pairs_only_on_the_reingest_path()
    {
        using (var reingest = JsonDocument.Parse(ContradictionDetector.BuildRemoveQuery("d:/docs/a.md", onlyOpen: true)))
        {
            var b = reingest.RootElement.GetProperty("query").GetProperty("bool");
            Assert.Equal(2, b.GetProperty("should").GetArrayLength());
            var spared = b.GetProperty("must_not")[0].GetProperty("terms").GetProperty("status");
            Assert.Equal(new[] { "dismissed", "resolved" }, spared.EnumerateArray().Select(e => e.GetString()));
        }

        using (var removal = JsonDocument.Parse(ContradictionDetector.BuildRemoveQuery("d:/docs/a.md", onlyOpen: false)))
        {
            Assert.Equal(0, removal.RootElement.GetProperty("query").GetProperty("bool").GetProperty("must_not").GetArrayLength());
        }
    }
}
