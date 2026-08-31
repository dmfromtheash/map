using System.Text;

namespace Map.Persistence.Tests.Support;

/// <summary>
/// Locates small JSON fragments in either indented or compact test fixtures. Production JSON
/// formatting is intentionally not part of the persistence compatibility contract.
/// </summary>
internal static class JsonTestMutations
{
    public static bool ContainsFragment(string json, string fragment) =>
        json.Contains(fragment, StringComparison.Ordinal)
        || json.Contains(Compact(fragment), StringComparison.Ordinal);

    public static string ReplaceFirst(string json, string fragment, string replacement)
    {
        var index = json.IndexOf(fragment, StringComparison.Ordinal);
        if (index >= 0)
        {
            return string.Concat(
                json.AsSpan(0, index), replacement, json.AsSpan(index + fragment.Length));
        }

        var compactFragment = Compact(fragment);
        index = json.IndexOf(compactFragment, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"JSON does not contain the expected fragment: {fragment}");
        }

        var compactReplacement = Compact(replacement);
        return string.Concat(
            json.AsSpan(0, index), compactReplacement,
            json.AsSpan(index + compactFragment.Length));
    }

    public static string ReplaceAll(string json, string fragment, string replacement)
    {
        if (json.Contains(fragment, StringComparison.Ordinal))
        {
            return json.Replace(fragment, replacement, StringComparison.Ordinal);
        }

        var compactFragment = Compact(fragment);
        if (!json.Contains(compactFragment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"JSON does not contain the expected fragment: {fragment}");
        }

        return json.Replace(compactFragment, Compact(replacement), StringComparison.Ordinal);
    }

    public static string InsertAfter(string json, string fragment, string addition)
    {
        var index = json.IndexOf(fragment, StringComparison.Ordinal);
        if (index >= 0)
        {
            return json.Insert(index + fragment.Length, addition);
        }

        var compactFragment = Compact(fragment);
        index = json.IndexOf(compactFragment, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"JSON does not contain the expected fragment: {fragment}");
        }

        return json.Insert(index + compactFragment.Length, Compact(addition));
    }

    private static string Compact(string fragment)
    {
        var result = new StringBuilder(fragment.Length);
        var inString = false;
        var escaped = false;

        foreach (var character in fragment)
        {
            if (inString)
            {
                result.Append(character);
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                result.Append(character);
            }
            else if (!char.IsWhiteSpace(character))
            {
                result.Append(character);
            }
        }

        return result.ToString();
    }
}
