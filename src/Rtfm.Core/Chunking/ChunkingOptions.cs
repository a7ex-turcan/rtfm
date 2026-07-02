namespace Rtfm.Core.Chunking;

/// <summary>
/// Tuning for <see cref="MarkdownChunker"/>. Sizes are in characters (roughly
/// chars/4 tokens). Chunks split on heading boundaries first; these limits only
/// bite when a single section's body is larger than <see cref="MaxChars"/>.
/// </summary>
public sealed record ChunkingOptions(int MaxChars = 1600, int OverlapChars = 200)
{
    public static ChunkingOptions Default { get; } = new();
}
