using Rtfm.Core.AzureDevOps;

namespace Rtfm.Core.Tests.AzureDevOps;

public class AdoSourceTests
{
    [Fact]
    public void Key_carries_the_lower_cased_organization()
    {
        Assert.Equal("ado://contoso/123", AdoSource.Key("Contoso", 123));
        Assert.Equal(AdoSource.Key("CONTOSO", 123), AdoSource.Key("contoso", 123));

        // Same id, different organizations → different keys (ids are only org-unique).
        Assert.NotEqual(AdoSource.Key("contoso", 123), AdoSource.Key("fabrikam", 123));
    }

    [Fact]
    public void IsAdoKey_recognizes_only_the_scheme()
    {
        Assert.True(AdoSource.IsAdoKey(AdoSource.Key("contoso", 1)));
        Assert.False(AdoSource.IsAdoKey("jira://AEXP-1"));
        Assert.False(AdoSource.IsAdoKey("d:/docs/a.md"));
    }

    [Theory]
    [InlineData("4512", 4512)]
    [InlineData("#4512", 4512)]
    [InlineData(" 42 ", 42)]
    [InlineData("https://dev.azure.com/Contoso/Team%20Alpha/_workitems/edit/4512", 4512)]
    [InlineData("https://dev.azure.com/Contoso/Team%20Alpha/_workitems/edit/4512/", 4512)]
    [InlineData("https://dev.azure.com/Contoso/Team%20Alpha/_boards/board/t/Team/Stories?workitem=777", 777)]
    [InlineData("https://dev.azure.com/Contoso/_apis/wit/workItems/55", 55)]
    public void ParseWorkItemId_accepts_ids_mentions_and_browser_urls(string input, int expected)
        => Assert.Equal(expected, AdoSource.ParseWorkItemId(input));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("AEXP-19")]
    [InlineData("https://dev.azure.com/Contoso/Team/_git/repo")]
    public void ParseWorkItemId_rejects_what_names_no_work_item(string input)
        => Assert.Null(AdoSource.ParseWorkItemId(input));

    [Theory]
    [InlineData("https://dev.azure.com/Contoso", "https://dev.azure.com/Contoso", "Contoso")]
    [InlineData("https://dev.azure.com/Contoso/Team%20Alpha/_workitems/edit/1", "https://dev.azure.com/Contoso", "Contoso")]
    [InlineData("dev.azure.com/Contoso/", "https://dev.azure.com/Contoso", "Contoso")]
    [InlineData("Contoso", "https://dev.azure.com/Contoso", "Contoso")]
    [InlineData("https://contoso.visualstudio.com/Team", "https://contoso.visualstudio.com", "contoso")]
    public void Organization_parse_reduces_any_url_to_the_organization(string input, string url, string name)
    {
        var org = AdoOrganization.Parse(input);
        Assert.Equal(url, org.Url);
        Assert.Equal(name, org.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://dev.azure.com/")]
    [InlineData("https://tfs.contoso.local/tfs/DefaultCollection")]
    [InlineData("https://contoso.atlassian.net")]
    public void Organization_parse_rejects_non_services_hosts(string input)
        => Assert.Throws<ArgumentException>(() => AdoOrganization.Parse(input));
}
