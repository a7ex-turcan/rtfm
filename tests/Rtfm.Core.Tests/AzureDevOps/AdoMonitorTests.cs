using Rtfm.Core.AzureDevOps;
using Rtfm.Core.Contradictions;

namespace Rtfm.Core.Tests.AzureDevOps;

public class AdoMonitorTests
{
    [Fact]
    public void SelectChanged_returns_only_items_whose_revision_moved_up()
    {
        var monitor = new AdoMonitor();
        monitor.Set(new MonitoredWorkItem(1, 5, Full: true));
        monitor.Set(new MonitoredWorkItem(2, 5, Full: false));
        monitor.Set(new MonitoredWorkItem(3, 5, Full: false));

        // 1 moved up; 2 unchanged; 3 vanished from the poll (deleted/hidden) → untouched.
        var changed = monitor.SelectChanged(new Dictionary<int, int> { [1] = 6, [2] = 5 });

        Assert.Equal([1], changed);
    }

    [Fact]
    public void Store_round_trips_and_removes()
    {
        var project = $"test-ado-{Guid.NewGuid():N}";
        try
        {
            var monitor = new AdoMonitor { LastPolledAt = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero) };
            monitor.Set(new MonitoredWorkItem(4512, 74, Full: true));
            monitor.Set(new MonitoredWorkItem(4000, 7, Full: false));
            AdoMonitorStore.Save(project, monitor);

            var loaded = AdoMonitorStore.Load(project);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(new MonitoredWorkItem(4512, 74, true), loaded.Items[4512]);
            Assert.Equal(monitor.LastPolledAt, loaded.LastPolledAt);

            Assert.True(AdoMonitorStore.Remove(project));
            Assert.Equal(0, AdoMonitorStore.Load(project).Count);
        }
        finally
        {
            AdoMonitorStore.Remove(project);
        }
    }

    [Fact]
    public void Config_store_round_trips_without_any_secret()
    {
        var project = $"test-ado-{Guid.NewGuid():N}";
        try
        {
            var config = new AdoConfig(AdoFixtures.Org, AdoConfig.AuthAzCli, Tenant: "contoso.onmicrosoft.com");
            AdoConfigStore.Save(project, config);

            var loaded = AdoConfigStore.Load(project);
            Assert.Equal(config, loaded);
            Assert.False(loaded!.UsesPat);
            Assert.Equal("Contoso", loaded.Organization);
            Assert.Contains(AdoConfigStore.List(), e => e.Project == project);
        }
        finally
        {
            AdoConfigStore.Remove(project);
        }
    }

    [Fact]
    public void Pat_reference_expands_lazily_and_fails_loudly_when_unset()
    {
        var variable = $"RTFM_TEST_PAT_{Guid.NewGuid():N}";
        var config = new AdoConfig(AdoFixtures.Org, AdoConfig.AuthPat, Token: $"${{{variable}}}");

        Assert.True(config.UsesPat);
        Assert.Throws<InvalidDataException>(() => config.ResolveToken());

        Environment.SetEnvironmentVariable(variable, "secret");
        try
        {
            Assert.Equal("secret", config.ResolveToken());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}

public class AdoRecordChunkTests
{
    private const string Title = "User Story 4512: Portal> Admin >Users - Invites Tab";
    private const string Key = "ado://contoso/4512";

    [Theory]
    [InlineData(Title, true)]                                                          // lead (metadata) chunk
    [InlineData(Title + " > Linked work items", true)]
    [InlineData(Title + " > Links", true)]
    [InlineData(Title + " > Development > Pull requests > PR 1: Fix > thing", true)]
    [InlineData(Title + " > Development > Commits", true)]
    [InlineData(Title + " > Description", false)]
    [InlineData(Title + " > Acceptance criteria", false)]
    [InlineData(Title + " > Comment by Lee, 2026-10-03 14:05", false)]
    public void Classifies_by_the_section_after_a_title_that_itself_contains_separators(string headingPath, bool isRecord)
        => Assert.Equal(isRecord, ContradictionDetector.IsRecordChunk(Key, headingPath, Title));

    [Fact]
    public void Only_azure_devops_sources_are_ever_exempt()
    {
        Assert.False(ContradictionDetector.IsRecordChunk("d:/docs/a.md", "Doc > Development", "Doc"));
        Assert.False(ContradictionDetector.IsRecordChunk("jira://AEXP-1", "AEXP-1: x > Development", "AEXP-1: x"));
    }

    [Fact]
    public void An_unmatchable_title_falls_back_to_eligible()
    {
        Assert.False(ContradictionDetector.IsRecordChunk(Key, "Something else > Development", Title));
        Assert.False(ContradictionDetector.IsRecordChunk(Key, Title + " > Development", null));
    }

    [Fact]
    public void EvaluateCandidates_skips_a_record_candidate_and_takes_the_next()
    {
        var chunk = new Rtfm.Core.Chunking.Chunk(0, "d:/docs/new.md", "Roles", "The default role is super-admin.", "New",
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero), "p");

        var response = System.Text.Json.JsonSerializer.Serialize(new
        {
            hits = new
            {
                hits = new object[]
                {
                    new
                    {
                        _score = 0.99,
                        _source = new
                        {
                            source_path = Key, ordinal = 0, heading_path = Title, title = Title,
                            content = Title + "\n\n> Type: User Story · State: Closed", source_modified_at = "2025-09-01T00:00:00Z",
                        },
                    },
                    new
                    {
                        _score = 0.90,
                        _source = new
                        {
                            source_path = Key, ordinal = 1, heading_path = Title + " > Description", title = Title,
                            content = Title + " > Description\n\nThe default role is admin.", source_modified_at = "2025-09-01T00:00:00Z",
                        },
                    },
                },
            },
        });

        var nomination = ContradictionDetector.EvaluateCandidates(chunk, response);

        Assert.NotNull(nomination);
        Assert.Equal(1, nomination.Pair.B.Ordinal);
    }
}
