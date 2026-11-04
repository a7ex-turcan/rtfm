namespace Rtfm.Core.AzureDevOps;

/// <summary>
/// One pulled work item (§2.18). Multi-line fields arrive as HTML or — when the
/// organization has Markdown fields enabled — as Markdown; each carries its
/// format (from the response's <c>multilineFieldsFormat</c>) so the renderer
/// routes HTML through the shared ReverseMarkdown tail and passes Markdown
/// through.
/// </summary>
public sealed record AdoWorkItem(
    int Id,
    int Rev,
    string Title,
    string? WorkItemType,
    string? State,
    string? Reason,
    string? AssignedTo,
    string? CreatedBy,
    DateTimeOffset? Created,
    DateTimeOffset? Changed,
    string? Priority,
    string? AreaPath,
    string? IterationPath,
    IReadOnlyList<string> Tags,
    string? TeamProject,
    int CommentCount,
    int? ParentId,
    IReadOnlyList<AdoRichField> RichFields,
    IReadOnlyList<AdoLink> Links,
    IReadOnlyList<AdoArtifactLink> Artifacts,
    IReadOnlyList<AdoHyperlink> Hyperlinks,
    IReadOnlyList<AdoComment> Comments,
    string? WebUrl);

/// <summary>A rendered multi-line field (Description, Acceptance Criteria, Repro Steps, or a custom one).</summary>
public sealed record AdoRichField(string ReferenceName, string Label, string Content, bool IsMarkdown);

/// <summary>A work item → work item link, e.g. <c>Parent</c>, <c>Child</c>, <c>Related</c>, <c>Successor</c>.</summary>
public sealed record AdoLink(string Relationship, int Id);

/// <summary>A plain hyperlink attached to the work item (the URL, plus its comment when one was given).</summary>
public sealed record AdoHyperlink(string Url, string? Comment);

/// <summary>One discussion comment: author, machine date, and body in its declared format.</summary>
public sealed record AdoComment(string Author, DateTimeOffset? Created, string Text, bool IsMarkdown);

/// <summary>What a <c>vstfs:///Git/…</c> artifact link points at.</summary>
public enum AdoArtifactKind
{
    PullRequest,
    Commit,
    Branch,
}

/// <summary>
/// A parsed <c>ArtifactLink</c> relation — <c>vstfs:///Git/PullRequestId/{project}/{repo}/{id}</c>,
/// <c>…/Commit/{project}/{repo}/{sha}</c>, or <c>…/Ref/{project}/{repo}/GB{branch}</c>.
/// <see cref="Value"/> is the PR id, commit sha, or branch name. Nothing here is
/// fetched yet: the link alone names the artifact, and
/// <see cref="AdoClient.FetchDevelopmentAsync"/> resolves it.
/// </summary>
public sealed record AdoArtifactLink(AdoArtifactKind Kind, string ProjectId, string RepositoryId, string Value, DateTimeOffset? LinkedAt);

/// <summary>
/// A work item's development record: the pull requests, commits, and branches
/// linked to it, resolved through the Git REST API. The <c>Omitted*</c> counts
/// are what the per-item caps left out and <see cref="Unreadable"/> what could
/// not be fetched — both are <em>rendered</em>, never silently dropped (§5).
/// </summary>
public sealed record AdoDevelopment(
    IReadOnlyList<AdoPullRequest> PullRequests,
    IReadOnlyList<AdoCommit> Commits,
    IReadOnlyList<AdoBranch> Branches,
    int OmittedPullRequests = 0,
    int OmittedCommits = 0,
    int Unreadable = 0)
{
    /// <summary>No development data — none linked, or none readable.</summary>
    public static readonly AdoDevelopment None = new([], [], []);

    public bool IsEmpty => PullRequests.Count == 0 && Commits.Count == 0 && Branches.Count == 0
        && OmittedPullRequests == 0 && OmittedCommits == 0 && Unreadable == 0;
}

/// <summary>
/// One linked pull request. Its <see cref="Description"/> is the highest-value
/// field here: in Azure DevOps the PR description, not the (often
/// <c>Merged PR 123: …</c>) merge commit, is where the implementation rationale
/// gets written.
/// </summary>
public sealed record AdoPullRequest(
    int Id,
    string Title,
    string? Status,
    bool IsDraft,
    string? Author,
    string? SourceBranch,
    string? TargetBranch,
    string? Repository,
    string? Url,
    DateTimeOffset? Created,
    DateTimeOffset? Closed,
    string? Description,
    IReadOnlyList<AdoReviewer> Reviewers);

/// <summary>A pull request reviewer and their vote (10 approved, 5 approved with suggestions, -5 waiting, -10 rejected).</summary>
public sealed record AdoReviewer(string Name, int Vote);

/// <summary>One linked commit, with its full message.</summary>
public sealed record AdoCommit(string Id, string? Author, DateTimeOffset? AuthoredAt, string? Message, string? Repository, string? Url);

/// <summary>One linked branch.</summary>
public sealed record AdoBranch(string Name, string? Repository);

/// <summary>Raised for an Azure DevOps failure the CLI should report cleanly (auth, not-found, HTTP error).</summary>
public sealed class AdoException(string message) : Exception(message);
