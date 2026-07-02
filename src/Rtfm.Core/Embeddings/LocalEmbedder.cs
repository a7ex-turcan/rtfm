using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Rtfm.Core.Indexing;

namespace Rtfm.Core.Embeddings;

/// <summary>
/// Local, in-process sentence embeddings (§2.10 Tier 2): all-MiniLM-L6-v2 via
/// ONNX Runtime on CPU. WordPiece-tokenizes the text, runs the transformer, then
/// mean-pools the last hidden state and L2-normalizes — the standard
/// sentence-transformers recipe, and with normalized vectors the mapping's
/// <c>l2</c> space ranks identically to cosine. Model files come from
/// <see cref="EmbeddingModelStore"/> (downloaded once, cached per user).
/// Loading is lazy: constructing this is free; the first embed loads (and if
/// needed downloads) the model. Safe for concurrent <see cref="Embed"/> calls.
/// </summary>
public sealed class LocalEmbedder : ITextEmbedder, IDisposable
{
    /// <summary>MiniLM's maximum sequence length in WordPiece tokens.</summary>
    private const int MaxTokens = 512;

    private readonly EmbeddingModelStore _store;
    private readonly Lock _initLock = new();

    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;

    public LocalEmbedder(EmbeddingModelStore? store = null) => _store = store ?? new EmbeddingModelStore();

    public int Dimension => RtfmIndex.VectorDimension;

    /// <summary>
    /// Downloads (if needed) and loads the model up front so the first
    /// <see cref="Embed"/> doesn't absorb the cost at an awkward moment.
    /// Optional — <see cref="Embed"/> self-initializes.
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        await _store.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        EnsureLoaded();
    }

    public float[] Embed(string text)
    {
        EnsureLoaded();

        var ids = _tokenizer!.EncodeToIds(text, MaxTokens, out _, out _);
        var n = ids.Count;

        var inputIds = new DenseTensor<long>([1, n]);
        var attentionMask = new DenseTensor<long>([1, n]);
        var tokenTypeIds = new DenseTensor<long>([1, n]);
        for (var i = 0; i < n; i++)
        {
            inputIds[0, i] = ids[i];
            attentionMask[0, i] = 1;
            tokenTypeIds[0, i] = 0;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var results = _session!.Run(inputs);
        var hidden = results[0].AsTensor<float>(); // [1, n, dim]

        return MeanPoolAndNormalize(hidden, n, Dimension);
    }

    /// <summary>Mean over the sequence dimension, then L2-normalize. Internal for tests.</summary>
    internal static float[] MeanPoolAndNormalize(Tensor<float> hidden, int seqLen, int dim)
    {
        var vector = new float[dim];
        for (var t = 0; t < seqLen; t++)
        {
            for (var d = 0; d < dim; d++)
            {
                vector[d] += hidden[0, t, d];
            }
        }

        for (var d = 0; d < dim; d++)
        {
            vector[d] /= seqLen;
        }

        return NormalizeL2(vector);
    }

    /// <summary>Scales the vector to unit length (no-op for the zero vector). Internal for tests.</summary>
    internal static float[] NormalizeL2(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return vector;
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

            // Sync-over-async is fine here: console/MCP hosts have no
            // synchronization context, and callers wanting async warm-up use
            // EnsureReadyAsync instead.
            _store.EnsureAvailableAsync().GetAwaiter().GetResult();

            _tokenizer = BertTokenizer.Create(_store.VocabPath);
            _session = new InferenceSession(_store.ModelPath);
        }
    }

    public void Dispose() => _session?.Dispose();
}
