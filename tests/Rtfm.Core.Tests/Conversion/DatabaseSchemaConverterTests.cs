using System.Text;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

/// <summary>
/// Descriptor logic only — live pulls are exercised by the Phase 20 validation
/// against a throwaway container, not by unit tests (no network here).
/// </summary>
public class DatabaseSchemaConverterTests
{
    [Fact]
    public void Descriptor_parses_and_expands_environment_placeholders()
    {
        Environment.SetEnvironmentVariable("RTFM_TEST_DB_PW", "s3cret");
        try
        {
            var descriptor = DbDescriptor.Parse(
                """{ "provider": "postgres", "connectionString": "Host=x;Password=${RTFM_TEST_DB_PW};Database=d", "name": "Ref DB", "schemas": ["public"] }""");

            Assert.Equal("postgres", descriptor.Provider);
            Assert.Equal("Host=x;Password=s3cret;Database=d", descriptor.ConnectionString);
            Assert.Equal("Ref DB", descriptor.Name);
            Assert.NotNull(descriptor.Schemas);
            Assert.Equal(["public"], descriptor.Schemas);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTFM_TEST_DB_PW", null);
        }
    }

    [Fact]
    public void Missing_environment_variable_fails_loudly()
        => Assert.Throws<InvalidDataException>(() => DbDescriptor.ExpandEnvironment("Password=${RTFM_DEFINITELY_NOT_SET_VAR}"));

    [Theory]
    [InlineData("""{ "provider": "", "connectionString": "x" }""")]
    [InlineData("""{ "provider": "postgres" }""")]
    [InlineData("""{ }""")]
    public void Incomplete_descriptors_are_rejected(string json)
        => Assert.Throws<InvalidDataException>(() => DbDescriptor.Parse(json));

    [Fact]
    public void Unknown_provider_is_rejected_with_a_pointed_message()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{ "provider": "oracle", "connectionString": "x" }"""));

        var ex = Assert.Throws<NotSupportedException>(() => new DatabaseSchemaConverter().Convert(stream, "ref.rtfmdb"));
        Assert.Contains("sqlserver", ex.Message);
    }

    [Fact]
    public void Detector_routes_rtfmdb_by_extension()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""{ "provider": "postgres" }"""));
        Assert.Equal(SourceFormat.Database, FormatDetector.Detect("ref.rtfmdb", stream));
    }
}
