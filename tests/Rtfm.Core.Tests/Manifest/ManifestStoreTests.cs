using Rtfm.Core.Manifest;

namespace Rtfm.Core.Tests.Manifest;

public class ManifestStoreTests
{
    [Fact]
    public void Load_returns_empty_manifest_when_none_saved_yet()
    {
        // A folder/project pair that has never been watched.
        var store = ManifestStore.For(NewTempFolder(), "never-watched");

        var manifest = store.Load();

        Assert.Equal(0, manifest.Count);
    }

    [Fact]
    public void Save_then_load_round_trips_entries()
    {
        var store = ManifestStore.For(NewTempFolder(), "payments");
        var manifest = new DocumentManifest();
        manifest.Set("d:/docs/a.doc", new ManifestEntry(12345, 678));
        manifest.Set("d:/docs/b.md", new ManifestEntry(99, 1));

        store.Save(manifest);
        var reloaded = store.Load();

        Assert.Equal(2, reloaded.Count);
        Assert.True(reloaded.TryGet("d:/docs/a.doc", out var a));
        Assert.Equal(new ManifestEntry(12345, 678), a);
        Assert.True(reloaded.TryGet("d:/docs/b.md", out var b));
        Assert.Equal(new ManifestEntry(99, 1), b);
    }

    [Fact]
    public void Same_folder_different_projects_keep_independent_manifests()
    {
        var folder = NewTempFolder();
        var payments = ManifestStore.For(folder, "payments");
        var billing = ManifestStore.For(folder, "billing");

        Assert.NotEqual(payments.FilePath, billing.FilePath);

        var m = new DocumentManifest();
        m.Set("d:/docs/x.doc", new ManifestEntry(1, 2));
        payments.Save(m);

        Assert.Equal(1, payments.Load().Count);
        Assert.Equal(0, billing.Load().Count);
    }

    [Fact]
    public void PurgeManifests_removes_only_the_projects_manifests()
    {
        // Unique project names: FindManifests scans the real per-user manifests
        // directory, so tests must never touch a name a human might use.
        var doomed = $"test-purge-{Guid.NewGuid():n}";
        var spared = $"test-keep-{Guid.NewGuid():n}";

        try
        {
            var manifest = new DocumentManifest();
            manifest.Set("d:/docs/x.doc", new ManifestEntry(1, 2));

            ManifestStore.For(NewTempFolder(), doomed).Save(manifest);
            ManifestStore.For(NewTempFolder(), doomed).Save(manifest); // second folder, same project
            ManifestStore.For(NewTempFolder(), spared).Save(manifest);

            Assert.Equal(2, ManifestStore.FindManifests(doomed).Count);

            var removed = ManifestStore.PurgeManifests(doomed);

            Assert.Equal(2, removed);
            Assert.Empty(ManifestStore.FindManifests(doomed));
            Assert.Single(ManifestStore.FindManifests(spared));
        }
        finally
        {
            ManifestStore.PurgeManifests(doomed);
            ManifestStore.PurgeManifests(spared);
        }
    }

    // A distinct, non-existent folder per test so runs don't collide.
    private static string NewTempFolder()
        => Path.Combine(Path.GetTempPath(), "rtfm-tests", Guid.NewGuid().ToString("n"));
}
