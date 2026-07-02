using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Rtfm.Core.Embeddings;

/// <summary>
/// Scores (query, passage) pairs for reranking (§2.10 Tier 3, Phase 11).
/// Higher = more relevant. Implemented locally, in-process, like everything
/// else in this namespace.
/// </summary>
public interface IReranker
{
    /// <summary>Relevance score (raw logit) for each passage against the query, index-aligned.</summary>
    IReadOnlyList<float> Score(string query, IReadOnlyList<string> passages);
}

/// <summary>
/// The Tier 3 reranker: ms-marco-MiniLM-L-6-v2 cross-encoder via ONNX Runtime
/// on CPU (Phase 11). Unlike the bi-encoder embedder, a cross-encoder reads the
/// query and the passage *together* — [CLS] query [SEP] passage [SEP] with
/// segment ids marking the halves — and emits one relevance logit, which is
/// what makes it more precise (and too slow to run over the whole corpus; it
/// only ever sees the hybrid query's shortlist). Same lazy-load +
/// model-store machinery as <see cref="LocalEmbedder"/>. Safe for concurrent
/// <see cref="Score"/> calls.
/// </summary>
public sealed class CrossEncoder : IReranker, IDisposable
{
    /// <summary>BERT's maximum sequence length; pairs are truncated to fit (passage side gives way).</summary>
    private const int MaxTokens = 512;

    private readonly EmbeddingModelStore _store;
    private readonly Lock _initLock = new();

    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;

    public CrossEncoder(EmbeddingModelStore? store = null) => _store = store ?? EmbeddingModelStore.ForReranker();

    /// <summary>Downloads (if needed) and loads the model up front. Optional — <see cref="Score"/> self-initializes.</summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        await _store.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        EnsureLoaded();
    }

    public IReadOnlyList<float> Score(string query, IReadOnlyList<string> passages)
    {
        EnsureLoaded();

        // Tokenize the query once; each passage pairs against it.
        var queryIds = _tokenizer!.EncodeToIds(query);
        var scores = new float[passages.Count];

        for (var i = 0; i < passages.Count; i++)
        {
            var passageIds = _tokenizer.EncodeToIds(passages[i]);
            var (ids, typeIds) = BuildPair(queryIds, passageIds, _tokenizer.SeparatorTokenId, MaxTokens);
            scores[i] = Run(ids, typeIds);
        }

        return scores;
    }

    /// <summary>
    /// Combines two already-encoded sequences (each <c>[CLS] … [SEP]</c>) into
    /// the BERT pair form <c>[CLS] a [SEP] b [SEP]</c> with segment ids 0/1.
    /// When over <paramref name="maxTokens"/>, the passage side is truncated
    /// and the final [SEP] preserved. Internal for tests.
    /// </summary>
    internal static (int[] Ids, int[] TypeIds) BuildPair(
        IReadOnlyList<int> queryWithSpecials, IReadOnlyList<int> passageWithSpecials, int sepTokenId, int maxTokens)
    {
        // Drop the passage's leading [CLS]; keep everything else.
        var ids = new List<int>(queryWithSpecials.Count + passageWithSpecials.Count - 1);
        ids.AddRange(queryWithSpecials);
        for (var i = 1; i < passageWithSpecials.Count; i++)
        {
            ids.Add(passageWithSpecials[i]);
        }

        if (ids.Count > maxTokens)
        {
            ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));
            ids.Add(sepTokenId);
        }

        var typeIds = new int[ids.Count];
        for (var i = queryWithSpecials.Count; i < typeIds.Length; i++)
        {
            typeIds[i] = 1; // second segment: the passage
        }

        return (ids.ToArray(), typeIds);
    }

    private float Run(int[] ids, int[] typeIds)
    {
        var n = ids.Length;
        var inputIds = new DenseTensor<long>([1, n]);
        var attentionMask = new DenseTensor<long>([1, n]);
        var tokenTypeIds = new DenseTensor<long>([1, n]);
        for (var i = 0; i < n; i++)
        {
            inputIds[0, i] = ids[i];
            attentionMask[0, i] = 1;
            tokenTypeIds[0, i] = typeIds[i];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var results = _session!.Run(inputs);
        return results[0].AsTensor<float>()[0, 0]; // single relevance logit
    }

    private void EnsureLoaded()
    {
        if (_session is not null)
        {
            return;
        }

        lock (_initLock)
        {
            if (_session is not null)
            {
                return;
            }

            // Same sync-over-async rationale as LocalEmbedder: console/MCP
            // hosts have no synchronization context.
            _store.EnsureAvailableAsync().GetAwaiter().GetResult();

            _tokenizer = BertTokenizer.Create(_store.VocabPath);
            _session = new InferenceSession(_store.ModelPath);
        }
    }

    public void Dispose() => _session?.Dispose();
}
