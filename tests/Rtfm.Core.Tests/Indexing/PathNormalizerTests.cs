using Rtfm.Core.Indexing;

namespace Rtfm.Core.Tests.Indexing;

public class PathNormalizerTests
{
    [Fact]
    public void Normalizes_to_absolute_lowercase_forward_slashes()
    {
        var key = PathNormalizer.Normalize("docs/Sub/File.DOC");

        Assert.DoesNotContain('\\', key);
        Assert.Equal(key.ToLowerInvariant(), key);
        Assert.True(Path.IsPathRooted(key));
        Assert.EndsWith("docs/sub/file.doc", key);
    }

    [Fact]
    public void Is_stable_across_casing_and_separator_differences()
    {
        // The whole point of §2.12: index-time and watch-time keys must match.
        var a = PathNormalizer.Normalize("docs/Sub/File.DOC");
        var b = PathNormalizer.Normalize("DOCS\\sub\\file.doc");

        Assert.Equal(a, b);
        Assert.Equal(a, PathNormalizer.Normalize(a)); // idempotent
    }
}
