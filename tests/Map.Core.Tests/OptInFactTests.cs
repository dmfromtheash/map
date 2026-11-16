namespace Map.Core.Tests;

public sealed class OptInFactTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_configuration_is_a_defect_skip(string? value)
    {
        var variable = "MAP_OPTIN_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(variable, value);
            var fact = new OptInFactAttribute("owner-library report", variable);
            Assert.StartsWith("defect:", fact.Skip);
            Assert.Contains("owner-library report", fact.Skip);
            Assert.Contains(variable, fact.Skip);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public void Every_required_variable_must_be_configured_before_the_diagnostic_runs()
    {
        var output = "MAP_OPTIN_TEST_" + Guid.NewGuid().ToString("N");
        var library = "MAP_OPTIN_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(output, "local-output");
            var missingLibrary = new OptInFactAttribute("owner-library report", output, library);
            Assert.StartsWith("defect:", missingLibrary.Skip);
            Assert.Contains(library, missingLibrary.Skip);
            Assert.DoesNotContain(output, missingLibrary.Skip);
            Environment.SetEnvironmentVariable(library, "local-library");
            Assert.Null(new OptInFactAttribute("owner-library report", output, library).Skip);
        }
        finally
        {
            Environment.SetEnvironmentVariable(output, null);
            Environment.SetEnvironmentVariable(library, null);
        }
    }
}
