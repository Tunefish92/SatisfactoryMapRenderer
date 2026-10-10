using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SatisfactoryMapRenderer.Save.Properties;

namespace SatisfactoryMapRenderer.Save;

/// <summary>Writes every decoded object with all properties to a JSON file.</summary>
public static class SaveJsonExporter
{
    private static readonly JsonSerializerOptions ReflectionOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new ObjectReferenceConverter() },
    };

    private sealed class ObjectReferenceConverter : JsonConverter<ObjectReference>
    {
        public override ObjectReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, ObjectReference value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.PathName);
    }

    public static void Export(SaveGame save, string path)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        w.WriteStartObject();
        w.WritePropertyName("header");
        JsonSerializer.Serialize(w, save.Header);
        w.WriteNumber("uncompressedBodySize", save.UncompressedBodySize);
        w.WriteNumber("chunkCount", save.ChunkCount);

        w.WriteStartArray("grids");
        foreach (var grid in save.Grids)
        {
            w.WriteStartObject();
            w.WriteString("name", grid.Name);
            w.WriteNumber("cellSize", grid.CellSize);
            w.WriteNumber("cellCount", grid.Cells.Count);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("levels");
        foreach (var level in save.Levels)
        {
            w.WriteStartObject();
            w.WriteString("name", level.Name);
            if (level.LevelSaveVersion is { } lv)
                w.WriteNumber("levelSaveVersion", lv);
            WriteReferences(w, "collectables", level.Collectables);
            w.WriteStartObject("destroyedActors");
            foreach (var (key, list) in level.DestroyedActors)
                WriteReferences(w, key, list);
            w.WriteEndObject();

            w.WriteStartArray("objects");
            foreach (var obj in level.Objects)
                WriteObject(w, obj);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        WriteReferences(w, "unresolvedWorldSaveData", save.UnresolvedWorldSaveData);
        w.WriteEndObject();
    }

    private static void WriteReferences(Utf8JsonWriter w, string name, List<ObjectReference> refs)
    {
        w.WriteStartArray(name);
        foreach (var r in refs)
            w.WriteStringValue(r.PathName);
        w.WriteEndArray();
    }

    private static void WriteObject(Utf8JsonWriter w, SaveObject obj)
    {
        w.WriteStartObject();
        w.WriteString("class", obj.ClassName);
        w.WriteString("instance", obj.InstanceName);
        if (obj.ObjectSaveVersion != 0)
            w.WriteNumber("saveVersion", obj.ObjectSaveVersion);
        if (obj is SaveEntity e)
        {
            w.WriteString("type", "actor");
            w.WritePropertyName("position");
            WriteVector(w, e.Transform.Translation);
            w.WriteStartArray("rotation");
            w.WriteNumberValue(e.Transform.Rotation.X);
            w.WriteNumberValue(e.Transform.Rotation.Y);
            w.WriteNumberValue(e.Transform.Rotation.Z);
            w.WriteNumberValue(e.Transform.Rotation.W);
            w.WriteEndArray();
            w.WritePropertyName("scale");
            WriteVector(w, e.Transform.Scale);
            w.WriteBoolean("wasPlacedInLevel", e.WasPlacedInLevel);
            if (!e.Parent.IsEmpty)
                w.WriteString("parent", e.Parent.PathName);
            if (e.Components.Count > 0)
                WriteReferences(w, "components", e.Components);
        }
        else if (obj is SaveComponent c)
        {
            w.WriteString("type", "component");
            w.WriteString("parent", c.ParentEntityName);
        }

        w.WritePropertyName("properties");
        WriteProperties(w, obj.Properties);

        if (obj.SpecialData is not null)
        {
            w.WritePropertyName("specialData");
            WriteValue(w, obj.SpecialData);
        }
        if (obj.TrailingData is { Length: > 0 } trailing)
            w.WriteNumber("trailingBytes", trailing.Length);
        if (obj.ParseError is not null)
            w.WriteString("parseError", obj.ParseError);
        w.WriteEndObject();
    }

    private static void WriteProperties(Utf8JsonWriter w, PropertyList list)
    {
        w.WriteStartObject();
        foreach (var p in list)
        {
            w.WritePropertyName(p.Index == 0 ? p.Name : $"{p.Name}[{p.Index}]");
            WriteValue(w, p.Value);
        }
        w.WriteEndObject();
    }

    private static void WriteVector(Utf8JsonWriter w, Vector3 v)
    {
        w.WriteStartArray();
        w.WriteNumberValue(Math.Round(v.X, 3));
        w.WriteNumberValue(Math.Round(v.Y, 3));
        w.WriteNumberValue(Math.Round(v.Z, 3));
        w.WriteEndArray();
    }

    private static void WriteNumber(Utf8JsonWriter w, double d)
    {
        if (double.IsFinite(d))
            w.WriteNumberValue(d);
        else
            w.WriteStringValue(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case string s: w.WriteStringValue(s); break;
            case byte v: w.WriteNumberValue(v); break;
            case sbyte v: w.WriteNumberValue(v); break;
            case short v: w.WriteNumberValue(v); break;
            case ushort v: w.WriteNumberValue(v); break;
            case int v: w.WriteNumberValue(v); break;
            case uint v: w.WriteNumberValue(v); break;
            case long v: w.WriteNumberValue(v); break;
            case ulong v: w.WriteNumberValue(v); break;
            case float v: WriteNumber(w, v); break;
            case double v: WriteNumber(w, v); break;
            case Guid g: w.WriteStringValue(g.ToString("N")); break;
            case ObjectReference r: w.WriteStringValue(r.PathName); break;
            case EnumValue e: w.WriteStringValue(e.Value); break;
            case TextValue t: w.WriteStringValue(t.Text); break;
            case SoftObjectValue so: w.WriteStringValue(so.ToString()); break;
            case Vector3 v: WriteVector(w, v); break;
            case Quaternion q:
                w.WriteStartArray();
                WriteNumber(w, q.X); WriteNumber(w, q.Y); WriteNumber(w, q.Z); WriteNumber(w, q.W);
                w.WriteEndArray();
                break;
            case PropertyList list: WriteProperties(w, list); break;
            case StructValue sv: WriteValue(w, sv.Data); break;
            case ArrayValue a:
                w.WriteStartArray();
                foreach (var item in a.Items) WriteValue(w, item);
                w.WriteEndArray();
                break;
            case SetValue s:
                w.WriteStartArray();
                foreach (var item in s.Items) WriteValue(w, item);
                w.WriteEndArray();
                break;
            case MapValue m:
                w.WriteStartArray();
                foreach (var (k, v) in m.Entries)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("key"); WriteValue(w, k);
                    w.WritePropertyName("value"); WriteValue(w, v);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                break;
            case RawValue raw:
                w.WriteStartObject();
                w.WriteString("raw", Convert.ToBase64String(raw.Bytes));
                w.WriteString("reason", raw.Reason);
                w.WriteEndObject();
                break;
            case DynamicStructValue ds:
                w.WriteStartObject();
                w.WriteString("structType", ds.StructType.PathName);
                if (ds.Properties is not null) { w.WritePropertyName("properties"); WriteProperties(w, ds.Properties); }
                if (ds.Raw is not null) w.WriteNumber("rawBytes", ds.Raw.Length);
                w.WriteEndObject();
                break;
            case InventoryItemValue item:
                w.WriteStartObject();
                w.WriteString("item", item.Item.PathName);
                if (item.ItemState is not null) { w.WritePropertyName("state"); WriteValue(w, item.ItemState); }
                if (item.LegacyItemStateActor is { IsEmpty: false } legacy) w.WriteString("stateActor", legacy.PathName);
                w.WriteEndObject();
                break;
            case LightweightBuildables lb:
                w.WriteStartObject();
                w.WriteNumber("runtimeVersion", lb.RuntimeVersion);
                w.WriteStartArray("groups");
                foreach (var g in lb.Groups)
                {
                    w.WriteStartObject();
                    w.WriteString("class", g.ClassName);
                    w.WriteStartArray("instances");
                    foreach (var inst in g.Instances)
                    {
                        w.WriteStartObject();
                        w.WritePropertyName("position"); WriteVector(w, inst.Transform.Translation);
                        w.WriteNumber("yawDegrees", Math.Round(inst.Transform.Rotation.Yaw * 180 / Math.PI, 2));
                        if (inst.Recipe.Length > 0) w.WriteString("recipe", inst.Recipe);
                        if (inst.SwatchSlot.Length > 0) w.WriteString("swatch", inst.SwatchSlot);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                break;
            default:
                // Records (colors, boxes, wire connections, ...) serialize fine via reflection.
                JsonSerializer.Serialize(w, value, value.GetType(), ReflectionOptions);
                break;
        }
    }
}
