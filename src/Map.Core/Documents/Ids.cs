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

/// <summary>Stable identity of an editable vector node.</summary>
public readonly record struct VectorNodeId(Guid Value)
{
    public static VectorNodeId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static VectorNodeId Parse(string s) => new(Guid.ParseExact(s, "D"));
}

/// <summary>Stable identity of a nondestructive gap in a line or shape stroke.</summary>
public readonly record struct PortalId(Guid Value)
{
    public static PortalId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static PortalId Parse(string s) => new(Guid.ParseExact(s, "D"));
}

/// <summary>Stable identity of one operand in a nondestructive shape expression.</summary>
public readonly record struct ShapeRegionId(Guid Value)
{
    public static ShapeRegionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static ShapeRegionId Parse(string s) => new(Guid.ParseExact(s, "D"));
}

public readonly record struct NoteId(Guid Value)
{
    public static NoteId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static NoteId Parse(string s) => new(Guid.ParseExact(s, "D"));
}

public readonly record struct GridId(Guid Value)
{
    public static GridId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static GridId Parse(string s) => new(Guid.ParseExact(s, "D"));
}
