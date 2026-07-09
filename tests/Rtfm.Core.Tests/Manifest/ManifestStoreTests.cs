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
        // Manifests persist in the real per-user store — always a unique
        // test-prefixed project name, always cleaned up (rtfm status lists
        // leftovers, so litter is visible to the user).
        var project = NewTestProject();
        try
        {
            var store = ManifestStore.For(NewTempFolder(), project);
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
        finally
        {
            ManifestStore.PurgeManifests(project);
        }
    }

    [Fact]
    public void Same_folder_different_projects_keep_independent_manifests()
    {
        var first = NewTestProject();
        var second = NewTestProject();
        try
        {
            var folder = NewTempFolder();
            var firstStore = ManifestStore.For(folder, first);
            var secondStore = ManifestStore.For(folder, second);

            Assert.NotEqual(firstStore.FilePath, secondStore.FilePath);

            var m = new DocumentManifest();
            m.Set("d:/docs/x.doc", new ManifestEntry(1, 2));
            firstStore.Save(m);

            Assert.Equal(1, firstStore.Load().Count);
            Assert.Equal(0, secondStore.Load().Count);
        }
        finally
        {
            ManifestStore.PurgeManifests(first);
            ManifestStore.PurgeManifests(second);
        }
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

    [Fact]
    public void ListAll_exposes_original_cased_folder_for_watch_all()
    {
        // `watch --all` re-opens folders from the manifests; the manifest key is
        // lower-cased (§2.12) but must not be what we try to open on a
        // case-sensitive OS — the original casing is preserved for that.
        var project = NewTestProject();
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "rtfm-tests", "MixedCase-" + Guid.NewGuid().ToString("n"));
            var m = new DocumentManifest();
            m.Set("d:/docs/a.md", new ManifestEntry(1, 2));
            ManifestStore.For(folder, project).Save(m);

            var info = ManifestStore.ListAll().Single(x => x.Project == project);

            var expected = Path.GetFullPath(folder);
            Assert.Equal(expected, info.OpenableFolder);          // original casing, native separators
            Assert.Contains("MixedCase", info.OpenableFolder);    // not lower-cased
            Assert.Equal(expected.Replace('\\', '/').ToLowerInvariant(), info.Folder); // key stays normalized
        }
        finally
        {
            ManifestStore.PurgeManifests(project);
        }
    }

    // A distinct, non-existent folder per test so runs don't collide.
    private static string NewTempFolder()
        => Path.Combine(Path.GetTempPath(), "rtfm-tests", Guid.NewGuid().ToString("n"));

    // A unique project name a human would never use — safe to purge blindly.
    private static string NewTestProject()
        => $"test-manifest-{Guid.NewGuid():n}";
}
