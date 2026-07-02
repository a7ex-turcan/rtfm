namespace Rtfm.Core.Embeddings;

/// <summary>
/// Turns text into a fixed-size vector for the <c>content_vector</c> knn field
/// (§2.10 Tier 2). Implementations run locally and in-process — no external
/// API. The interface exists so ingest/search stay testable without loading a
/// real model.
/// </summary>
public interface ITextEmbedder
{
    /// <summary>The vector length produced — must match the index mapping's dimension.</summary>
    int Dimension { get; }

    /// <summary>Embeds one text into an L2-normalized vector.</summary>
    float[] Embed(string text);
}
