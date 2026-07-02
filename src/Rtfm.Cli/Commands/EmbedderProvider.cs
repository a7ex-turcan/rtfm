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
}
