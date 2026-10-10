using SatisfactoryMapRenderer.Save;
using SatisfactoryMapRenderer.Save.Properties;
using SatisfactoryMapRenderer.Tests.TestSupport;

namespace SatisfactoryMapRenderer.Tests;

public sealed class PropertyReaderTests
{
    public static TheoryData<bool, bool> Formats => new()
    {
        { false, false }, // classic tags, float vectors (≤ U7)
        { false, true },  // classic tags, double vectors (U8 – 1.0)
        { true, true },   // UE 5.4+ complete type names (1.1+)
    };

    private static (PropertyList List, ReadContext Context) Read(bool complete, bool doubles, params P[] properties)
    {
        var format = new PropertyFormat(complete, doubles);
        byte[] bytes = SaveWriter.Build(w => P.WriteList(w, properties, format));
        var ctx = new ReadContext
        {
            SaveVersion = complete ? 58 : doubles ? 42 : 36,
            UE5PackageVersion = complete ? SyntheticSave.UE5PackageVersion : 1000,
        };
        var reader = new BinaryDataReader(bytes);
        var list = new PropertyReader(reader, ctx).ReadList();
        Assert.Equal(bytes.Length, reader.Position);
        return (list, ctx);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Decodes_scalar_properties(bool complete, bool doubles)
    {
        var (list, ctx) = Read(complete, doubles,
            new PInt("Int", -42),
            new PInt64("Long", 1L << 40),
            new PFloat("Float", 0.25f),
            new PDouble("Double", 1e100),
            new PBool("True", true),
            new PBool("False", false),
            new PStr("Str", "Grüße ✓"),
            new PByte("Byte", 200),
            new PEnum("Enum", "EResourcePurity", "RP_Pure"),
            new PText("Text", "My Station"),
            new PObj("Obj", "Persistent_Level:PersistentLevel.Thing"));

        Assert.Equal(-42, list.Get("Int"));
        Assert.Equal(1L << 40, list.Get("Long"));
        Assert.Equal(0.25f, list.Get("Float"));
        Assert.Equal(1e100, list.Get("Double"));
        Assert.Equal(true, list.GetBool("True"));
        Assert.Equal(false, list.GetBool("False"));
        Assert.Equal("Grüße ✓", list.GetText("Str"));
        Assert.Equal((byte)200, list.Get("Byte"));
        Assert.Equal(new EnumValue("EResourcePurity", "RP_Pure"), list.Get("Enum"));
        Assert.Equal("My Station", list.GetText("Text"));
        Assert.Equal("Persistent_Level:PersistentLevel.Thing", list.GetReference("Obj")?.PathName);
        Assert.Equal(11, ctx.PropertiesDecoded);
        Assert.Equal(0, ctx.PropertiesFailed);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Decodes_containers_and_nested_structs(bool complete, bool doubles)
    {
        var (list, _) = Read(complete, doubles,
            new PIntArray("Ints", 1, 2, 3),
            new PObjArray("Refs", "A:PersistentLevel.X", "A:PersistentLevel.Y"),
            new PByteArray("Bytes", [9, 8, 7]),
            new PStructArray("Structs", "MyItem",
                [new PInt("Count", 5), new PVector("Where", 1.5, -2, 3)],
                [new PInt("Count", 6)]),
            new PVectorSet("mRemovalLocations", (1, 2, 3)),
            new PStrIntMap("Map", ("a", 1), ("b", 2)),
            new PStruct("Nested", "Outer", new PStruct("Inner", "InnerType", new PStr("Deep", "value"))));

        Assert.Equal([1, 2, 3], list.GetArray("Ints")!.Items.Cast<int>());
        Assert.Equal(["A:PersistentLevel.X", "A:PersistentLevel.Y"], list.GetArray("Refs")!.Items.Cast<ObjectReference>().Select(r => r.PathName));
        Assert.Equal([9, 8, 7], list.GetArray("Bytes")!.Items.Cast<byte>().Select(b => (int)b));

        var structs = list.GetArray("Structs")!.Items.Cast<StructValue>().ToList();
        Assert.All(structs, s => Assert.Equal("MyItem", s.StructType));
        var first = (PropertyList)structs[0].Data!;
        Assert.Equal(5, first.Get("Count"));
        Assert.Equal(new Vector3(1.5, -2, 3), ((StructValue)first.Get("Where")!).Data);
        Assert.Equal(6, ((PropertyList)structs[1].Data!).Get("Count"));

        Assert.Equal(new Vector3(1, 2, 3), Assert.Single(Assert.IsType<SetValue>(list.Get("mRemovalLocations")).Items));

        var map = Assert.IsType<MapValue>(list.Get("Map"));
        Assert.Equal([new("a", 1), new("b", 2)], map.Entries.Select(e => new KeyValuePair<object?, object?>(e.Key, e.Value)));

        var outer = (PropertyList)((StructValue)list.Get("Nested")!).Data!;
        var inner = (PropertyList)((StructValue)outer.Get("Inner")!).Data!;
        Assert.Equal("value", inner.GetText("Deep"));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Corrupt_property_is_kept_raw_and_the_list_continues(bool complete, bool doubles)
    {
        // An IntProperty with only 2 payload bytes cannot be decoded.
        var (list, ctx) = Read(complete, doubles,
            new PRaw("Broken", "IntProperty", [1, 2]),
            new PInt("After", 7));

        var raw = Assert.IsType<RawValue>(list.Get("Broken"));
        Assert.Equal([1, 2], raw.Bytes);
        Assert.Equal(7, list.Get("After"));
        Assert.Equal(1, ctx.PropertiesFailed);
    }

    [Fact]
    public void Unknown_property_type_is_kept_raw()
    {
        var (list, ctx) = Read(false, false, new PRaw("Mystery", "FancyNewProperty", [1, 2, 3, 4, 5]), new PInt("After", 1));

        Assert.Equal([1, 2, 3, 4, 5], Assert.IsType<RawValue>(list.Get("Mystery")).Bytes);
        Assert.Equal(1, list.Get("After"));
        Assert.Equal(1, ctx.PropertiesFailed);
    }

    [Fact]
    public void Complete_tags_read_array_index_and_skip_property_guid()
    {
        // Hand-written UE 5.4+ tag: flags 0x01 (array index) | 0x02 (guid).
        byte[] bytes = SaveWriter.Build(w =>
        {
            w.Str("Indexed");
            new TypeNode("IntProperty").Write(w);
            w.I32(4).Byte(0x03).I32(2).Zeros(16).I32(99);
            w.Str("None");
        });
        var ctx = new ReadContext { SaveVersion = 58, UE5PackageVersion = 1014 };
        var list = new PropertyReader(new BinaryDataReader(bytes), ctx).ReadList();

        var property = Assert.Single(list);
        Assert.Equal(2, property.Index);
        Assert.Equal(99, property.Value);
    }

    /// <summary>
    /// 1.2.4 servers write map marker locations as a tagged X/Y/Z list although Vector_NetQuantize normally
    /// is binary; the tag's HasBinaryOrNativeSerialize flag tells the two apart.
    /// </summary>
    [Fact]
    public void Decodes_a_usually_binary_struct_written_as_property_list()
    {
        var (list, ctx) = Read(complete: true, doubles: true,
            new PStruct("Location", "Vector_NetQuantize", new PDouble("X", 1.5), new PDouble("Y", -2), new PDouble("Z", 300)),
            new PVector("Binary", 4, 5, 6));

        Assert.Empty(ctx.Underflows);
        Assert.Equal(0, ctx.PropertiesFailed);
        var tagged = Assert.IsType<PropertyList>(Assert.IsType<StructValue>(list.Get("Location")).Data);
        Assert.Equal(300, tagged.GetNumber("Z"));
        Assert.Equal(new Vector3(4, 5, 6), Assert.IsType<StructValue>(list.Get("Binary")).Data);
    }
}
