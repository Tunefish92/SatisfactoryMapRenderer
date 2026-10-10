namespace SatisfactoryMapRenderer.Save.Properties;

/// <summary>Version information that changes how properties are laid out. Set per object.</summary>
public sealed class ReadContext
{
    public int SaveVersion { get; set; }
    public int UE5PackageVersion { get; set; }

    /// <summary>UE 5.4+ tags: the type is written as a tree and flags replace the old per-type header.</summary>
    public bool CompleteTypeNames =>
        SaveVersion >= (int)SaveCustomVersion.SerializeDataPackageVersionAndCustomVersions
        && UE5PackageVersion >= UnrealVersions.PropertyTagCompleteTypeName;

    /// <summary>Since the switch to 64-bit archives, vectors inside structs are doubles.</summary>
    public bool DoublePrecision => SaveVersion >= (int)SaveCustomVersion.SwitchTo64BitSaveArchive;

    public int PropertiesDecoded;
    public int PropertiesFailed;

    /// <summary>Properties whose payload was longer than what was decoded (type → count), for diagnostics.</summary>
    public Dictionary<string, int> Underflows { get; } = [];
}

/// <summary>Decodes Unreal tagged property lists as used by Satisfactory saves.</summary>
public sealed class PropertyReader(BinaryDataReader r, ReadContext ctx)
{
    /// <summary>EPropertyTagFlags::HasBinaryOrNativeSerialize.</summary>
    private const byte TagHasBinaryOrNativeSerialize = 0x08;

    private sealed record Tag(string Name, PropertyTypeNode Type, int Size, int Index, byte Flags, bool BoolValue);

    public PropertyList ReadList()
    {
        var list = new PropertyList();
        while (true)
        {
            var tag = ReadTag();
            if (tag is null)
                return list;
            list.Add(ReadProperty(tag));
        }
    }

    private Tag? ReadTag()
    {
        string name = r.ReadString();
        if (name == "None" || name.Length == 0)
            return null;

        if (ctx.CompleteTypeNames)
        {
            var type = ReadTypeNode();
            int size = r.ReadInt32();
            byte flags = r.ReadByte();
            int index = (flags & 0x01) != 0 ? r.ReadInt32() : 0;
            if ((flags & 0x02) != 0)
                r.Skip(16); // property guid
            if ((flags & 0x04) != 0)
            {
                byte extensions = r.ReadByte();
                if ((extensions & 0x02) != 0)
                {
                    r.ReadByte();  // overridable operation
                    r.ReadInt32(); // experimental overridable logic (UBOOL)
                }
            }
            return new Tag(name, type, size, index, flags, (flags & 0x10) != 0);
        }

        string typeName = r.ReadString();
        int binarySize = r.ReadInt32();
        int arrayIndex = r.ReadInt32();
        PropertyTypeNode node;
        bool boolValue = false;
        switch (typeName)
        {
            case "ArrayProperty":
            case "SetProperty":
                node = new PropertyTypeNode(typeName, [PropertyTypeNode.Leaf(r.ReadString())]);
                break;
            case "StructProperty":
                node = new PropertyTypeNode(typeName, [PropertyTypeNode.Leaf(r.ReadString())]);
                r.Skip(16); // struct guid
                break;
            case "BoolProperty":
                boolValue = r.ReadByte() != 0;
                node = PropertyTypeNode.Leaf(typeName);
                break;
            case "ByteProperty":
            case "EnumProperty":
            {
                string enumName = r.ReadString();
                node = enumName == "None"
                    ? PropertyTypeNode.Leaf(typeName)
                    : new PropertyTypeNode(typeName, [PropertyTypeNode.Leaf(enumName)]);
                break;
            }
            case "MapProperty":
                node = new PropertyTypeNode(typeName, [PropertyTypeNode.Leaf(r.ReadString()), PropertyTypeNode.Leaf(r.ReadString())]);
                break;
            default:
                node = PropertyTypeNode.Leaf(typeName);
                break;
        }

        if (r.ReadByte() != 0)
            r.Skip(16); // property guid

        return new Tag(name, node, binarySize, arrayIndex, 0, boolValue);
    }

    private PropertyTypeNode ReadTypeNode()
    {
        string name = r.ReadString();
        int count = r.ReadInt32();
        if (count == 0)
            return PropertyTypeNode.Leaf(name);
        var children = new PropertyTypeNode[count];
        for (int i = 0; i < count; i++)
            children[i] = ReadTypeNode();
        return new PropertyTypeNode(name, children);
    }

    private Property ReadProperty(Tag tag)
    {
        var property = new Property { Name = tag.Name, Type = tag.Type, Index = tag.Index };
        int start = r.Position;
        int end = start + tag.Size;
        try
        {
            property.Value = ReadValue(tag);
            if (r.Position > end)
                throw new SaveFormatException($"read {r.Position - start} bytes, tag declared {tag.Size}");
            if (r.Position < end)
            {
                string key = $"{tag.Type} {tag.Name} ({end - r.Position} bytes)";
                ctx.Underflows[key] = ctx.Underflows.GetValueOrDefault(key) + 1;
                r.Position = end;
            }
            ctx.PropertiesDecoded++;
        }
        catch (Exception ex) when (ex is SaveFormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            r.Position = start;
            property.Value = new RawValue(r.ReadBytes(Math.Min(tag.Size, r.Remaining)), ex.Message);
            ctx.PropertiesFailed++;
        }
        return property;
    }

    private object? ReadValue(Tag tag)
    {
        var type = tag.Type;
        switch (type.Name)
        {
            case "BoolProperty": return tag.BoolValue;
            case "ByteProperty": return type.Children.Count == 0 || type.Children[0].Name == "None"
                ? r.ReadByte()
                : new EnumValue(type.Children[0].Name, r.ReadString());
            case "EnumProperty": return new EnumValue(type.Child(0)?.Name ?? "", r.ReadString());
            case "StructProperty":
            {
                string structType = type.Child(0)?.Name ?? "";
                // UE 5.4+ tags say whether a struct was written in its native binary form. A few that usually
                // are (e.g. a Vector_NetQuantize "Location") come as a tagged X/Y/Z property list instead.
                if (ctx.CompleteTypeNames && (tag.Flags & TagHasBinaryOrNativeSerialize) == 0 && IsBinaryStruct(structType))
                    return new StructValue(structType, ReadList());
                return new StructValue(structType, ReadStruct(structType, tag.Size));
            }
            case "ArrayProperty": return ReadArray(tag);
            case "SetProperty": return ReadSet(tag);
            case "MapProperty": return ReadMap(tag);
            default: return ReadScalar(type, tag.Name);
        }
    }

    /// <summary>Reads a value that has the same encoding standalone and as a container element.</summary>
    private object? ReadScalar(PropertyTypeNode type, string propertyName) => type.Name switch
    {
        "BoolProperty" => r.ReadByte() != 0,
        "ByteProperty" => r.ReadByte(),
        "Int8Property" => r.ReadSByte(),
        "Int16Property" => r.ReadInt16(),
        "UInt16Property" => r.ReadUInt16(),
        "IntProperty" => r.ReadInt32(),
        "UInt32Property" => r.ReadUInt32(),
        "Int64Property" => r.ReadInt64(),
        "UInt64Property" => r.ReadUInt64(),
        "FloatProperty" or "SingleProperty" => r.ReadSingle(),
        "DoubleProperty" => r.ReadDouble(),
        "StrProperty" or "NameProperty" => r.ReadString(),
        "EnumProperty" => new EnumValue(type.Child(0)?.Name ?? "", r.ReadString()),
        "ObjectProperty" or "InterfaceProperty" or "WeakObjectProperty" or "LazyObjectProperty" => ObjectReference.Read(r),
        "SoftObjectProperty" or "SoftClassProperty" => ReadSoftObject(),
        "TextProperty" => ReadText(),
        "StructProperty" => new StructValue(StructName(type), ReadStruct(StructName(type), -1)),
        _ => throw new SaveFormatException($"unsupported property type {type.Name}"),
    };

    private static string StructName(PropertyTypeNode type) => type.Child(0)?.Name ?? type.Name;

    private SoftObjectValue ReadSoftObject()
    {
        // UE5 splits the asset path into package + asset name; UE4 wrote it as one name.
        if (ctx.SaveVersion >= (int)SaveCustomVersion.UnrealEngine5)
        {
            string package = r.ReadString();
            string asset = r.ReadString();
            string sub = r.ReadString();
            return new SoftObjectValue(string.IsNullOrEmpty(asset) ? package : $"{package}.{asset}", sub);
        }
        return new SoftObjectValue(r.ReadString(), r.ReadString());
    }

    private ArrayValue ReadArray(Tag tag)
    {
        var elementType = tag.Type.Child(0) ?? throw new SaveFormatException("array without element type");
        int count = r.ReadInt32();
        if (count < 0 || count > r.Remaining)
            throw new SaveFormatException($"invalid array length {count}");

        var items = new List<object?>(count);
        switch (elementType.Name)
        {
            case "StructProperty":
            {
                string structType;
                if (ctx.CompleteTypeNames)
                {
                    structType = StructName(elementType);
                }
                else
                {
                    // Classic format repeats a full tag for the struct element type.
                    r.ReadString();              // property name
                    r.ReadString();              // "StructProperty"
                    r.ReadInt32();               // size of all elements
                    r.ReadInt32();               // index
                    structType = r.ReadString(); // element struct type
                    r.Skip(16);                  // guid
                    if (r.ReadByte() != 0)
                        r.Skip(16);
                }
                elementType = new PropertyTypeNode("StructProperty", [PropertyTypeNode.Leaf(structType)]);
                for (int i = 0; i < count; i++)
                    items.Add(new StructValue(structType, ReadStruct(structType, -1)));
                break;
            }
            case "ByteProperty":
            {
                // Enum-typed byte arrays store names instead of bytes; the payload size tells them apart.
                bool asBytes = tag.Size - 4 == count;
                for (int i = 0; i < count; i++)
                    items.Add(asBytes ? r.ReadByte() : r.ReadString());
                break;
            }
            default:
                for (int i = 0; i < count; i++)
                    items.Add(ReadScalar(elementType, tag.Name));
                break;
        }
        return new ArrayValue(elementType, items);
    }

    private SetValue ReadSet(Tag tag)
    {
        var elementType = tag.Type.Child(0) ?? throw new SaveFormatException("set without element type");
        int removed = r.ReadInt32();
        for (int i = 0; i < removed; i++)
            ReadSetElement(elementType, tag, 0);
        int count = r.ReadInt32();
        if (count < 0 || count > r.Remaining)
            throw new SaveFormatException($"invalid set length {count}");

        int elementSize = count > 0 ? (tag.Size - 8) / count : 0;
        var items = new List<object?>(count);
        for (int i = 0; i < count; i++)
            items.Add(ReadSetElement(elementType, tag, elementSize));
        return new SetValue(elementType, items);
    }

    private object? ReadSetElement(PropertyTypeNode elementType, Tag tag, int elementSize)
    {
        if (elementType.Name != "StructProperty")
            return ReadScalar(elementType, tag.Name);

        // Struct sets do not name their struct type in the classic format; infer it from the property.
        string structType = ctx.CompleteTypeNames ? StructName(elementType) : "";
        if (tag.Name == "mRemovalLocations" || structType is "Vector" or "Vector_NetQuantize")
            return elementSize == 24 ? Vector3.ReadDouble(r) : Vector3.ReadFloat(r);
        if (tag.Name is "mDestroyedPickups" or "mLootedDropPods" || structType == "Guid" || elementSize == 16)
            return r.ReadGuid();
        return new StructValue(structType, ReadStruct(structType, -1));
    }

    private MapValue ReadMap(Tag tag)
    {
        var keyType = tag.Type.Child(0) ?? throw new SaveFormatException("map without key type");
        var valueType = tag.Type.Child(1) ?? throw new SaveFormatException("map without value type");
        int removed = r.ReadInt32();
        for (int i = 0; i < removed; i++)
            ReadMapElement(keyType, tag.Name, isKey: true);
        int count = r.ReadInt32();
        if (count < 0 || count > r.Remaining)
            throw new SaveFormatException($"invalid map length {count}");

        var entries = new List<KeyValuePair<object?, object?>>(count);
        for (int i = 0; i < count; i++)
        {
            var key = ReadMapElement(keyType, tag.Name, isKey: true);
            var value = ReadMapElement(valueType, tag.Name, isKey: false);
            entries.Add(new(key, value));
        }
        return new MapValue(keyType, valueType, entries);
    }

    private object? ReadMapElement(PropertyTypeNode type, string propertyName, bool isKey)
    {
        if (type.Name != "StructProperty")
            return ReadScalar(type, propertyName);

        string structType = ctx.CompleteTypeNames ? StructName(type) : "";
        if (isKey && (propertyName is "mSaveData" or "mUnresolvedSaveData" || structType == "IntVector"))
            return new IntVectorValue(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        if (structType.Length > 0 && IsBinaryStruct(structType))
            return new StructValue(structType, ReadStruct(structType, -1));
        return new StructValue(structType, ReadList());
    }

    private static bool IsBinaryStruct(string structType) => structType is
        "Vector" or "Vector_NetQuantize" or "Vector_NetQuantize10" or "Vector_NetQuantize100" or "Vector_NetQuantizeNormal"
        or "Rotator" or "Vector2D" or "Quat" or "Vector4" or "Vector4D" or "Plane" or "Box" or "IntPoint" or "IntVector"
        or "LinearColor" or "Color" or "Guid" or "RailroadTrackPosition" or "TimerHandle" or "InventoryItem" or "FluidBox"
        or "SlateBrush" or "DateTime" or "Timespan" or "FrameNumber" or "SoftClassPath" or "SoftObjectPath"
        or "ClientIdentityInfo" or "PlayerInfoHandle" or "UniqueNetIdRepl";

    private Vector3 ReadVector() => ctx.DoublePrecision ? Vector3.ReadDouble(r) : Vector3.ReadFloat(r);
    private double ReadReal() => ctx.DoublePrecision ? r.ReadDouble() : r.ReadSingle();

    /// <param name="size">Payload size when known (standalone struct properties), otherwise -1.</param>
    private object? ReadStruct(string structType, int size)
    {
        switch (structType)
        {
            case "Vector":
            case "Rotator":
            case "Vector_NetQuantize":
            case "Vector_NetQuantize10":
            case "Vector_NetQuantize100":
            case "Vector_NetQuantizeNormal":
                // Some UE5 saves still carry float vectors; the size disambiguates.
                return size == 12 ? Vector3.ReadFloat(r) : size == 24 ? Vector3.ReadDouble(r) : ReadVector();
            case "Vector2D":
                return size == 8 ? new Vector2Value(r.ReadSingle(), r.ReadSingle()) : new Vector2Value(ReadReal(), ReadReal());
            case "Quat":
            case "Vector4":
            case "Vector4D":
            case "Plane":
                return size == 16 ? Quaternion.ReadFloat(r) : size == 32 ? Quaternion.ReadDouble(r)
                    : ctx.DoublePrecision ? Quaternion.ReadDouble(r) : Quaternion.ReadFloat(r);
            case "Box":
                return new BoxValue(ReadVector(), ReadVector(), r.ReadByte() != 0);
            case "IntPoint":
                return new IntPointValue(r.ReadInt32(), r.ReadInt32());
            case "IntVector":
                return new IntVectorValue(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            case "LinearColor":
                return new LinearColor(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            case "Color":
                return new Color(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
            case "Guid":
                return r.ReadGuid();
            case "RailroadTrackPosition":
                return new RailroadTrackPositionValue(ObjectReference.Read(r), r.ReadSingle(), r.ReadSingle());
            case "TimerHandle":
            case "SlateBrush":
                return r.ReadString();
            case "DateTime":
            case "Timespan":
                return r.ReadInt64();
            case "FrameNumber":
                return r.ReadInt32();
            case "FluidBox":
                return r.ReadSingle();
            case "SoftClassPath":
            case "SoftObjectPath":
                return ReadSoftObject();
            case "InventoryItem":
                return ReadInventoryItem();
            case "ClientIdentityInfo":
                return ReadClientIdentity();
            case "PlayerInfoHandle":
                return ReadPlayerInfoHandle(r, ctx);
            case "UniqueNetIdRepl":
                if (size < 0)
                    throw new SaveFormatException("UniqueNetIdRepl without known size");
                return new RawValue(r.ReadBytes(size), "UniqueNetIdRepl");
            default:
                return ReadList();
        }
    }

    private InventoryItemValue ReadInventoryItem()
    {
        var item = ObjectReference.Read(r);
        if (ctx.SaveVersion >= (int)SaveCustomVersion.RefactoredInventoryItemState)
            return new InventoryItemValue(item, ReadDynamicStruct(), null);
        return new InventoryItemValue(item, null, ObjectReference.Read(r));
    }

    /// <summary>FFGDynamicStruct: optional struct of arbitrary type with explicit payload size.</summary>
    public DynamicStructValue? ReadDynamicStruct()
    {
        if (r.ReadInt32() == 0)
            return null;
        var type = ObjectReference.Read(r);
        int payloadSize = r.ReadInt32();
        int start = r.Position;
        try
        {
            var list = ReadList();
            if (r.Position - start == payloadSize)
                return new DynamicStructValue(type, list, null);
        }
        catch (SaveFormatException)
        {
        }
        r.Position = start;
        return new DynamicStructValue(type, null, r.ReadBytes(payloadSize));
    }

    private ClientIdentityInfoValue ReadClientIdentity()
    {
        string offlineId = r.ReadString();
        int count = r.ReadInt32();
        var ids = new Dictionary<byte, byte[]>();
        for (int i = 0; i < count; i++)
        {
            byte platform = r.ReadByte();
            int length = r.ReadInt32();
            ids[platform] = r.ReadBytes(length);
        }
        return new ClientIdentityInfoValue(offlineId, ids);
    }

    public static PlayerInfoHandleValue ReadPlayerInfoHandle(BinaryDataReader r, ReadContext ctx)
    {
        byte provider = r.ReadByte();
        int index = ctx.SaveVersion >= (int)SaveCustomVersion.NewPlayerInfoHandleSerializationFormat ? r.ReadInt32() : r.ReadByte();
        return new PlayerInfoHandleValue(provider, index);
    }

    /// <summary>FText with its history; returns the best human readable representation.</summary>
    private TextValue ReadText()
    {
        r.ReadUInt32(); // flags
        sbyte history = r.ReadSByte();
        switch (history)
        {
            case -1:
                return new TextValue(history, r.ReadInt32() != 0 ? r.ReadString() : "");
            case 0: // Base
                r.ReadString(); // namespace
                r.ReadString(); // key
                return new TextValue(history, r.ReadString());
            case 1: // NamedFormat
            case 2: // OrderedFormat
            case 3: // ArgumentFormat
            {
                var format = ReadText().Text;
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string argName = history == 2 ? i.ToString() : r.ReadString();
                    string argValue = ReadFormatArgument();
                    format = format.Replace("{" + argName + "}", argValue);
                }
                return new TextValue(history, format);
            }
            case 10: // Transform
            {
                var source = ReadText();
                r.ReadByte();
                return new TextValue(history, source.Text);
            }
            case 11: // StringTableEntry
            {
                string table = r.ReadString();
                string key = r.ReadString();
                return new TextValue(history, $"{table}:{key}");
            }
            default:
                throw new SaveFormatException($"unsupported text history type {history}");
        }
    }

    private string ReadFormatArgument()
    {
        byte type = r.ReadByte();
        return type switch
        {
            0 => r.ReadInt64().ToString(),
            1 => r.ReadUInt64().ToString(),
            2 => r.ReadSingle().ToString(System.Globalization.CultureInfo.InvariantCulture),
            3 => r.ReadDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            4 => ReadText().Text,
            5 => r.ReadByte().ToString(),
            _ => throw new SaveFormatException($"unsupported text format argument {type}"),
        };
    }
}
