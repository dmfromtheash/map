namespace Map.Core.Documents;

/// <summary>Stable document identity. Serialized as the canonical lowercase "D" GUID string.</summary>
public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static DocumentId Parse(string s) => new(Guid.ParseExact(s, "D"));
}

/// <summary>Stable layer identity.</summary>
public readonly record struct LayerId(Guid Value)
{
    public static LayerId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static LayerId Parse(string s) => new(Guid.ParseExact(s, "D"));
}

/// <summary>Stable scene object identity.</summary>
public readonly record struct ObjectId(Guid Value)
{
    public static ObjectId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static ObjectId Parse(string s) => new(Guid.ParseExact(s, "D"));
}
