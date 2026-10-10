namespace SatisfactoryMapRenderer.Save.Properties;

/// <summary>A property type as a tree, e.g. ArrayProperty → StructProperty → SplinePointData.</summary>
public sealed record PropertyTypeNode(string Name, IReadOnlyList<PropertyTypeNode> Children)
{
    public static readonly IReadOnlyList<PropertyTypeNode> None = [];

    public static PropertyTypeNode Leaf(string name) => new(name, None);

    public PropertyTypeNode? Child(int index) => index < Children.Count ? Children[index] : null;

    public override string ToString() =>
        Children.Count == 0 ? Name : $"{Name}<{string.Join(",", Children.Select(c => c.ToString()))}>";
}

/// <summary>One decoded property. <see cref="Value"/> holds a CLR primitive or one of the value records below.</summary>
public sealed class Property
{
    public required string Name { get; init; }
    public required PropertyTypeNode Type { get; init; }
    public int Index { get; init; }
    public object? Value { get; set; }

    public override string ToString() => $"{Name} ({Type}) = {Value}";
}

public sealed class PropertyList : List<Property>
{
    public Property? Find(string name)
    {
        foreach (var p in this)
            if (p.Name == name)
                return p;
        return null;
    }

    public object? Get(string name) => Find(name)?.Value;

    public T? Get<T>(string name) where T : class => Find(name)?.Value as T;

    public bool TryGetStruct<T>(string name, out T value)
    {
        if (Find(name)?.Value is StructValue { Data: T data })
        {
            value = data;
            return true;
        }
        value = default!;
        return false;
    }

    public double? GetNumber(string name) => Find(name)?.Value switch
    {
        int i => i,
        long l => l,
        float f => f,
        double d => d,
        byte b => b,
        uint u => u,
        _ => null,
    };

    public bool? GetBool(string name) => Find(name)?.Value as bool?;

    /// <summary>Returns strings, enum values, names and texts as plain text.</summary>
    public string? GetText(string name) => Find(name)?.Value switch
    {
        string s => s,
        EnumValue e => e.Value,
        TextValue t => t.Text,
        _ => null,
    };

    public ObjectReference? GetReference(string name) => Find(name)?.Value as ObjectReference?;

    public ArrayValue? GetArray(string name) => Find(name)?.Value as ArrayValue;
}

public sealed record EnumValue(string EnumType, string Value)
{
    public override string ToString() => Value;
}

public sealed record TextValue(int HistoryType, string Text)
{
    public override string ToString() => Text;
}

public sealed record SoftObjectValue(string AssetPath, string SubPath)
{
    public override string ToString() => string.IsNullOrEmpty(SubPath) ? AssetPath : $"{AssetPath}:{SubPath}";
}

public sealed record StructValue(string StructType, object? Data)
{
    public override string ToString() => $"{StructType} {Data}";
}

public sealed record ArrayValue(PropertyTypeNode ElementType, IReadOnlyList<object?> Items);

public sealed record SetValue(PropertyTypeNode ElementType, IReadOnlyList<object?> Items);

public sealed record MapValue(PropertyTypeNode KeyType, PropertyTypeNode ValueType, IReadOnlyList<KeyValuePair<object?, object?>> Entries);

/// <summary>Undecodable payload, kept verbatim so nothing is silently lost.</summary>
public sealed record RawValue(byte[] Bytes, string Reason)
{
    public override string ToString() => $"<{Bytes.Length} raw bytes: {Reason}>";
}

public sealed record BoxValue(Vector3 Min, Vector3 Max, bool IsValid);

public sealed record IntVectorValue(int X, int Y, int Z);

public sealed record IntPointValue(int X, int Y);

public sealed record Vector2Value(double X, double Y);

public sealed record RailroadTrackPositionValue(ObjectReference Track, float Offset, float Forward);

public sealed record DynamicStructValue(ObjectReference StructType, PropertyList? Properties, byte[]? Raw);

public sealed record InventoryItemValue(ObjectReference Item, DynamicStructValue? ItemState, ObjectReference? LegacyItemStateActor);

public sealed record ClientIdentityInfoValue(string OfflineId, IReadOnlyDictionary<byte, byte[]> AccountIds);

public sealed record PlayerInfoHandleValue(byte ServiceProvider, int PlayerInfoTableIndex);
