using Rtfm.Core.Configuration;

namespace Rtfm.Core.Tests;

public class RtfmEnvironmentTests
{
    [Fact]
    public void ResolveOpenSearchUrl_DefaultsToLocalhost_WhenVariableUnset()
    {
        var original = Environment.GetEnvironmentVariable(RtfmEnvironment.OpenSearchUrlVariable);
        try
        {
            Environment.SetEnvironmentVariable(RtfmEnvironment.OpenSearchUrlVariable, null);

            var url = RtfmEnvironment.ResolveOpenSearchUrl();

            Assert.Equal(RtfmEnvironment.DefaultOpenSearchUrl, url.ToString().TrimEnd('/'));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RtfmEnvironment.OpenSearchUrlVariable, original);
        }
    }

    [Fact]
    public void ResolveOpenSearchUrl_UsesEnvironmentVariable_WhenSet()
    {
        var original = Environment.GetEnvironmentVariable(RtfmEnvironment.OpenSearchUrlVariable);
        try
        {
            Environment.SetEnvironmentVariable(RtfmEnvironment.OpenSearchUrlVariable, "http://opensearch.local:9201");

            var url = RtfmEnvironment.ResolveOpenSearchUrl();

            Assert.Equal("http://opensearch.local:9201/", url.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(RtfmEnvironment.OpenSearchUrlVariable, original);
        }
    }
}
