namespace Map.Core.Documents;

/// <summary>
/// Bounded, lossy sharing of value-like objects of the object model. A loaded map otherwise holds a
/// copy of every colour ("#FFFFFFFF"), blend name and 64-character asset hash in every object - about
/// 300 bytes per stamp - and a separate, value-equal <see cref="StampStyle"/> per copy of an asset.
/// <para>
/// The cache is a fixed two-way table: memory never grows past it whatever the user types, a value
/// that does not fit simply stays unshared, and threads racing on one slot only lose a sharing
/// opportunity. Values remain compared by value everywhere, so sharing changes nothing but memory.
/// </para>
/// </summary>
internal static class SharedValues
{
    public const int MaxStringLength = 64;
    private const int StringSlots = 1 << 16;
    private const int StyleSlots = 1 << 14;
    private static readonly string?[] Strings = new string?[StringSlots];
    private static readonly StampStyle?[] Styles = new StampStyle?[StyleSlots];

    /// <summary>Test hook: the calling thread builds objects without sharing (memory comparisons).</summary>
    [ThreadStatic] internal static bool BypassOnThisThread;

    /// <summary>Test hook: the calling thread keeps strings shared but styles separate.</summary>
    [ThreadStatic] internal static bool BypassStylesOnThisThread;

    public static string Share(string value)
    {
        if (value is null || value.Length > MaxStringLength || BypassOnThisThread) return value!;
        return Lookup(Strings, value, value.GetHashCode(), static (a, b) => string.Equals(a, b, StringComparison.Ordinal));
    }

    public static string? ShareOrNull(string? value) => value is null ? null : Share(value);

    public static StampStyle? Share(StampStyle? style)
    {
        if (style is null || BypassOnThisThread || BypassStylesOnThisThread) return style;
        return Lookup(Styles, style, style.GetHashCode(), StampStyle.IdenticalForSharing);
    }

    private static T Lookup<T>(T?[] table, T value, int hash, Func<T, T, bool> same) where T : class
    {
        var mask = table.Length - 1;
        var first = hash & mask;
        var second = first ^ 1;
        var a = Volatile.Read(ref table[first]);
        if (a is not null && (ReferenceEquals(a, value) || same(a, value))) return a;
        var b = Volatile.Read(ref table[second]);
        if (b is not null && (ReferenceEquals(b, value) || same(b, value))) return b;
        // Miss: fill an empty way, else replace one chosen by a hash bit (cheap pseudo-random replacement).
        var target = a is null ? first : b is null ? second : ((hash >> 16) & 1) == 0 ? first : second;
        Volatile.Write(ref table[target], value);
        return value;
    }
}
