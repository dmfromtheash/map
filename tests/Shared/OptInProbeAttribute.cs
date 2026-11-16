namespace Xunit;

/// <summary>Diagnostics are reported as skipped unless explicitly requested. The reason names the CI guard.</summary>
public sealed class OptInProbeAttribute : FactAttribute
{
    public OptInProbeAttribute(string variables, string ciSibling, string? requiredValue = null)
    {
        if (variables.Split(',').Any(variable => requiredValue is null
                ? string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))
                : Environment.GetEnvironmentVariable(variable) != requiredValue))
            Skip = $"opt-in: set {variables}; normal CI: {ciSibling}";
    }
}

public sealed class OptInProbeTheoryAttribute : TheoryAttribute
{
    public OptInProbeTheoryAttribute(string variable, string ciSibling)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1")
            Skip = $"opt-in: set {variable}=1; normal CI: {ciSibling}";
    }
}
