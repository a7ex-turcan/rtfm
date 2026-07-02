using System.Text.Json;
using Rtfm.Core.Notes;
using Rtfm.Core.Search;

namespace Rtfm.Core.Tests.Notes;

public class NotesTests
{
    private static readonly DateTimeOffset When = new(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Notes_index_mapping_has_analyzer_vector_and_anchor_fields()
    {
        using var doc = JsonDocument.Parse(NotesIndex.DefinitionJson);
        var props = doc.RootElement.GetProperty("mappings").GetProperty("properties");

        Assert.Equal("keyword", props.GetProperty("note_id").GetProperty("type").GetString());
        Assert.Equal("keyword", props.GetProperty("target_path").GetProperty("type").GetString());
        Assert.Equal("rtfm_technical", props.GetProperty("text").GetProperty("analyzer").GetString());
        Assert.Equal("knn_vector", props.GetProperty("content_vector").GetProperty("type").GetString());
        Assert.Equal(384, props.GetProperty("content_vector").GetProperty("dimension").GetInt32());
    }

    [Fact]
    public void Note_search_uses_knn_with_project_filter_when_vector_present()
    {
        using var doc = JsonDocument.Parse(NotesStore.BuildSearchQuery("q", [0.1f], "pam"));
        var knn = doc.RootElement.GetProperty("query").GetProperty("knn").GetProperty("content_vector");

        Assert.Equal("pam", knn.GetProperty("filter").GetProperty("term").GetProperty("project").GetString());
    }

    [Fact]
    public void Note_search_falls_back_to_lexical_match_without_vector()
    {
        using var doc = JsonDocument.Parse(NotesStore.BuildSearchQuery("default role", null, null));
        Assert.Equal("default role", doc.RootElement.GetProperty("query").GetProperty("match").GetProperty("text").GetString());
    }

    [Fact]
    public void NoteToHit_is_visibly_attributed_never_a_doc()
    {
        var note = new Note("abc123", "pam", "The default role is super-admin.", "d:/docs/rbac.doc", "alex", When);

        var hit = DocumentSearch.NoteToHit(note, 0.91);

        Assert.Equal("note", hit.Origin);
        Assert.Equal("note://abc123", hit.SourcePath);
        Assert.Equal("alex", hit.Author);
        Assert.Equal(note.Text, hit.Content);
        Assert.Equal(When, hit.SourceModifiedAt);
    }

    [Fact]
    public void MergeWithoutReranker_puts_notes_first()
    {
        var doc = new SearchHit(0.99, "pam", "d:/docs/a.doc", "H", null, "doc text", null);
        var note = DocumentSearch.NoteToHit(new Note("n1", "pam", "correction", null, "alex", When), 0.75);

        var merged = DocumentSearch.MergeWithoutReranker([doc], [note], topK: 2);

        Assert.Equal("note", merged[0].Origin);
        Assert.Equal("doc", merged[1].Origin);
    }

    [Fact]
    public void Annotate_attaches_only_matching_path_and_project()
    {
        var hits = new List<SearchHit>
        {
            new(0.9, "pam", "d:/docs/rbac.doc", "H", null, "text", null),
            new(0.8, "pam", "d:/docs/other.doc", "H", null, "text", null),
            new(0.7, "billing", "d:/docs/rbac.doc", "H", null, "text", null), // same path, wrong project
        };

        var anchored = new List<Note>
        {
            new("n1", "pam", "Outdated: role is super-admin now.", "d:/docs/rbac.doc", "alex", When),
        };

        var annotated = DocumentSearch.Annotate(hits, anchored);

        Assert.NotNull(annotated[0].Annotations);
        Assert.Single(annotated[0].Annotations!);
        Assert.Equal("n1", annotated[0].Annotations![0].Id);
        Assert.Null(annotated[1].Annotations);
        Assert.Null(annotated[2].Annotations);
    }
}
