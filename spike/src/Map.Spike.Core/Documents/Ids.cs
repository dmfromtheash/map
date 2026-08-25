namespace Map.Spike.Core.Documents;

/// <summary>Stable object identity. Serialized as a canonical lowercase GUID string.</summary>
public readonly record struct ObjectId(Guid Value)
{
    public static ObjectId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static ObjectId Parse(string s) => new(Guid.Parse(s));
}

/// <summary>Stable layer identity.</summary>
public readonly record struct LayerId(Guid Value)
{
    public static LayerId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static LayerId Parse(string s) => new(Guid.Parse(s));
}
