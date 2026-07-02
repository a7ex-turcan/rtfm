using Microsoft.ML.OnnxRuntime.Tensors;
using Rtfm.Core.Embeddings;

namespace Rtfm.Core.Tests.Embeddings;

/// <summary>
/// Covers the embedder's pure math. Running the real ONNX model needs an ~86 MB
/// download, so that is exercised by the live corpus validation, not unit tests.
/// </summary>
public class LocalEmbedderTests
{
    [Fact]
    public void MeanPoolAndNormalize_averages_over_the_sequence_then_normalizes()
    {
        // 2 tokens, 2 dims: token0 = (1, 0), token1 = (0, 1) → mean (0.5, 0.5) → unit (√2/2, √2/2).
        var hidden = new DenseTensor<float>(new[] { 1f, 0f, 0f, 1f }, [1, 2, 2]);

        var vector = LocalEmbedder.MeanPoolAndNormalize(hidden, seqLen: 2, dim: 2);

        Assert.Equal(2, vector.Length);
        Assert.Equal(Math.Sqrt(2) / 2, vector[0], precision: 6);
        Assert.Equal(Math.Sqrt(2) / 2, vector[1], precision: 6);
    }

    [Fact]
    public void NormalizeL2_produces_unit_length()
    {
        var vector = LocalEmbedder.NormalizeL2([3f, 4f]);

        Assert.Equal(0.6f, vector[0], precision: 6);
        Assert.Equal(0.8f, vector[1], precision: 6);
        Assert.Equal(1.0, Math.Sqrt(vector.Sum(v => (double)v * v)), precision: 6);
    }

    [Fact]
    public void NormalizeL2_leaves_zero_vector_alone()
    {
        var vector = LocalEmbedder.NormalizeL2([0f, 0f, 0f]);

        Assert.All(vector, v => Assert.Equal(0f, v));
    }
}
