using System.Security.Cryptography;
using System.Text;

namespace Rtfm.Core.Contradictions;

/// <summary>One side of a nominated pair: which chunk, of which document, saying what.</summary>
public sealed record ContradictionSide(
    string Path,
    int Ordinal,
    string Heading,
    DateTimeOffset? ModifiedAt,
    string Excerpt);

/// <summary>
/// A *nominated* contradiction (§2.13, Phase 12): two semantically-similar
/// chunks from different documents in the same project whose content may
/// disagree. RTFM only nominates — the LLM does the actual contradiction
/// reasoning at read time (§2.13 B), and nothing is auto-resolved. Side
/// <see cref="A"/> is the newer document by <c>source_modified_at</c>.
/// </summary>
public sealed record ContradictionPair(
    string Project,
    ContradictionSide A,
    ContradictionSide B,
    double Similarity,
    DateTimeOffset DetectedAt)
{
    /// <summary>
    /// Deterministic id: the same two chunks always produce the same id no
    /// matter which side was ingested last, so re-detection upserts instead of
    /// duplicating.
    /// </summary>
    public string Id
    {
        get
        {
            var keys = new[] { $"{A.Path}#{A.Ordinal}", $"{B.Path}#{B.Ordinal}" };
            Array.Sort(keys, StringComparer.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", keys))))[..16].ToLowerInvariant();
        }
    }
}
