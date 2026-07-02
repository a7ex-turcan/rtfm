using Rtfm.Core.Configuration;

namespace Rtfm.Core.Embeddings;

/// <summary>
/// Locates — and on first use downloads — the embedding model files (§2.10
/// Tier 2). The model is all-MiniLM-L6-v2 exported to ONNX (384 dims, matching
/// <see cref="Indexing.RtfmIndex.VectorDimension"/>): small, CPU-friendly, and
/// needs no query/passage prefixes. Files are cached per-user under
/// <see cref="RtfmEnvironment.ResolveModelDirectory"/> so the ~90 MB download
/// happens once per machine, not per index run. Downloads are atomic
/// (temp file + move) so a killed run can't leave a truncated model behind.
/// </summary>
public sealed class EmbeddingModelStore
{
    public const string ModelName = "all-MiniLM-L6-v2";

    private const string BaseUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main";

    private readonly string _directory;
    private readonly Action<string> _log;

    public EmbeddingModelStore(string? directory = null, Action<string>? log = null)
    {
        _directory = Path.Combine(directory ?? RtfmEnvironment.ResolveModelDirectory(), ModelName);
        _log = log ?? (_ => { });
    }

    public string ModelPath => Path.Combine(_directory, "model.onnx");

    public string VocabPath => Path.Combine(_directory, "vocab.txt");

    /// <summary>True when both model files are already cached locally.</summary>
    public bool IsCached => File.Exists(ModelPath) && File.Exists(VocabPath);

    /// <summary>
    /// Ensures both model files exist locally, downloading whichever is missing.
    /// Throws (with a pointed message) when the download fails — e.g. offline —
    /// so callers can fall back to Tier 1 explicitly rather than half-work.
    /// </summary>
    public async Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (IsCached)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        await DownloadIfMissingAsync(http, "onnx/model.onnx", ModelPath, cancellationToken).ConfigureAwait(false);
        await DownloadIfMissingAsync(http, "vocab.txt", VocabPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadIfMissingAsync(HttpClient http, string remoteFile, string localPath, CancellationToken cancellationToken)
    {
        if (File.Exists(localPath))
        {
            return;
        }

        var url = $"{BaseUrl}/{remoteFile}";
        _log($"Downloading {ModelName}/{remoteFile} (one-time, cached in {_directory}) …");

        var temp = localPath + ".tmp";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var target = File.Create(temp))
            {
                await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, localPath, overwrite: true);
            _log($"  downloaded {remoteFile} ({new FileInfo(localPath).Length / (1024 * 1024)} MB)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            TryDelete(temp);
            throw new InvalidOperationException(
                $"Could not download the embedding model from {url} — semantic search needs it. "
                + $"Check connectivity, or pre-provision the files and point {RtfmEnvironment.ModelDirectoryVariable} at them. ({ex.Message})",
                ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort — a stray .tmp is harmless and overwritten next run.
        }
    }
}
