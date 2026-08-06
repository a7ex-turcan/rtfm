namespace Rtfm.Core.Jira;

/// <summary>
/// A ticket's <b>Development</b> panel: the branches, pull requests, and commits
/// a linked source host (Bitbucket, GitHub, GitLab…) has associated with it.
///
/// <para>This is the one part of a ticket that does not come from the documented
/// issue resource — see <see cref="JiraClient.FetchDevelopmentAsync"/> for where
/// it comes from and why that matters. Commit <em>messages</em> are the reason
/// it is worth pulling at all: a well-written one carries the implementation
/// rationale that exists in no wiki page and no ticket comment.</para>
/// </summary>
public sealed record JiraDevelopment(
    IReadOnlyList<JiraPullRequest> PullRequests,
    IReadOnlyList<JiraBranch> Branches,
    IReadOnlyList<JiraCommit> Commits)
{
    /// <summary>No development data — the ticket has none, or it could not be read.</summary>
    public static readonly JiraDevelopment None = new([], [], []);

    public bool IsEmpty => PullRequests.Count == 0 && Branches.Count == 0 && Commits.Count == 0;
}

/// <summary>One pull request linked to a ticket.</summary>
public sealed record JiraPullRequest(
    string Name,
    string? Status,
    string? Author,
    string? SourceBranch,
    string? DestinationBranch,
    string? Repository,
    string? Url,
    DateTimeOffset? LastUpdate,
    IReadOnlyList<JiraReviewer> Reviewers);

/// <summary>A pull request reviewer and whether they have approved it.</summary>
public sealed record JiraReviewer(string Name, bool Approved);

/// <summary>One branch created for a ticket.</summary>
public sealed record JiraBranch(string Name, string? Repository, string? Url);

/// <summary>
/// One commit associated with a ticket. <see cref="Message"/> is the full commit
/// message (subject + body), which is the highest-value field here.
/// </summary>
public sealed record JiraCommit(
    string Id,
    string DisplayId,
    string? Author,
    DateTimeOffset? AuthoredAt,
    string? Message,
    string? Url,
    string? Repository,
    bool Merge);
