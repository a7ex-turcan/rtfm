using Rtfm.Core.Embeddings;

namespace Rtfm.Cli.Commands;

/// <summary>
/// One place the CLI commands get their <see cref="LocalEmbedder"/> from, so
/// index/watch/search degrade identically: if the model can't be readied
/// (offline first run), warn on stderr and continue lexical-only rather than
/// fail the command (§2.10 — Tier 1 must keep working without the embedding
/// weight).
/// </summary>
internal static class EmbedderProvider
{
    public static async Task<LocalEmbedder?> TryCreateAsync()
    {
        var embedder = new LocalEmbedder(new EmbeddingModelStore(log: Console.Error.WriteLine));
        try
        {
            await embedder.EnsureReadyAsync().ConfigureAwait(false);
            return embedder;
        }
        catch (Exception ex)
        {
            embedder.Dispose();
            Console.Error.WriteLine($"WARNING: semantic tier disabled for this run: {ex.Message}");
            return null;
        }
    }

    /// <summary>Tier 3 reranker (Phase 11), same degradation rule: unavailable → search keeps the fused order.</summary>
    public static async Task<CrossEncoder?> TryCreateRerankerAsync()
    {
        var reranker = new CrossEncoder(EmbeddingModelStore.ForReranker(log: Console.Error.WriteLine));
        try
        {
            await reranker.EnsureReadyAsync().ConfigureAwait(false);
            return reranker;
        }
        catch (Exception ex)
        {
            reranker.Dispose();
            Console.Error.WriteLine($"WARNING: reranking disabled for this run: {ex.Message}");
            return null;
        }
    }
}
