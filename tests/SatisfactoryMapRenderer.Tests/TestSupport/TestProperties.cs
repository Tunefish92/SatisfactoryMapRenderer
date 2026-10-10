namespace SatisfactoryMapRenderer.Tests.TestSupport;

/// <param name="Complete">UE 5.4+ property tags with type trees and flags.</param>
/// <param name="Doubles">Vectors inside structs are 64-bit (save version 41+).</param>
public sealed record PropertyFormat(bool Complete, bool Doubles);

public sealed record TypeNode(string Name, params TypeNode[] Children)
{
    public void Write(SaveWriter w)
    {
        w.Str(Name).I32(Children.Length);
        foreach (var child in Children)
            child.Write(w);
    }
}

/// <summary>Test-side description of a property that can be serialized in either tag format.</summary>
public abstract record P(string Name)
{
    public static void WriteList(SaveWriter w, IEnumerable<P> properties, PropertyFormat f)
    {
        foreach (var p in properties)
            p.Write(w, f);
        w.Str("None");
    }

    public void Write(SaveWriter w, PropertyFormat f)
    {
        byte[] value = SaveWriter.Build(v => WriteValue(v, f));
        w.Str(Name);
        if (f.Complete)
        {
            CompleteType.Write(w);
            w.I32(value.Length);
            w.Byte(CompleteFlags);
        }
        else
        {
            w.Str(CompleteType.Name);
            w.I32(value.Length);
            w.I32(0);
            WriteClassicHeader(w);
            w.Byte(0); // no property guid
        }
        w.Bytes(value);
    }

    protected abstract TypeNode CompleteType { get; }
    protected virtual byte CompleteFlags => 0;
    protected virtual void WriteClassicHeader(SaveWriter w) { }
    public abstract void WriteValue(SaveWriter w, PropertyFormat f);

    protected static TypeNode Struct(string name) =>
        new("StructProperty", new TypeNode(name, new TypeNode("/Script/CoreUObject")));
}

public sealed record PInt(string Name, int Value) : P(Name)
{
    protected override TypeNode CompleteType => new("IntProperty");
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.I32(Value);
}

public sealed record PInt64(string Name, long Value) : P(Name)
{
    protected override TypeNode CompleteType => new("Int64Property");
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.I64(Value);
}

public sealed record PFloat(string Name, float Value) : P(Name)
{
    protected override TypeNode CompleteType => new("FloatProperty");
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.F32(Value);
}

public sealed record PDouble(string Name, double Value) : P(Name)
{
    protected override TypeNode CompleteType => new("DoubleProperty");
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.F64(Value);
}

public sealed record PBool(string Name, bool Value) : P(Name)
{
    protected override TypeNode CompleteType => new("BoolProperty");
    protected override byte CompleteFlags => (byte)(Value ? 0x10 : 0);
    protected override void WriteClassicHeader(SaveWriter w) => w.Byte((byte)(Value ? 1 : 0));
    public override void WriteValue(SaveWriter w, PropertyFormat f) { }
}

public sealed record PStr(string Name, string Value) : P(Name)
{
    protected override TypeNode CompleteType => new("StrProperty");
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.Str(Value);
}

public sealed record PObj(string Name, string Path) : P(Name)
{
    protected override TypeNode CompleteType => new("ObjectProperty", new TypeNode("/Script/CoreUObject.Object"));
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.Ref(Path);
}

public sealed record PEnum(string Name, string EnumType, string Value) : P(Name)
{
    protected override TypeNode CompleteType => new("EnumProperty", new TypeNode(EnumType, new TypeNode("/Script/FactoryGame")), new TypeNode("ByteProperty"));
    protected override void WriteClassicHeader(SaveWriter w) => w.Str(EnumType);
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.Str(Value);
}

public sealed record PByte(string Name, byte Value) : P(Name)
{
    protected override TypeNode CompleteType => new("ByteProperty");
    protected override void WriteClassicHeader(SaveWriter w) => w.Str("None");
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.Byte(Value);
}

public sealed record PText(string Name, string Value) : P(Name)
{
    protected override TypeNode CompleteType => new("TextProperty");
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.U32(0).SByte(-1).I32(1).Str(Value);
}

public sealed record PVector(string Name, double X, double Y, double Z) : P(Name)
{
    protected override TypeNode CompleteType => Struct("Vector");
    // HasBinaryOrNativeSerialize: the game sets it for structs written in their native binary form.
    protected override byte CompleteFlags => 0x08;
    protected override void WriteClassicHeader(SaveWriter w) => w.Str("Vector").Zeros(16);
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.Vector(X, Y, Z, f.Doubles);
}

/// <summary>A struct serialized as a nested property list.</summary>
public sealed record PStruct(string Name, string StructType, params P[] Fields) : P(Name)
{
    protected override TypeNode CompleteType => new("StructProperty", new TypeNode(StructType, new TypeNode("/Script/FactoryGame")));
    protected override void WriteClassicHeader(SaveWriter w) => w.Str(StructType).Zeros(16);
    public override void WriteValue(SaveWriter w, PropertyFormat f) => WriteList(w, Fields, f);
}

public sealed record PObjArray(string Name, params string[] Paths) : P(Name)
{
    protected override TypeNode CompleteType => new("ArrayProperty", new TypeNode("ObjectProperty", new TypeNode("/Script/CoreUObject.Object")));
    protected override void WriteClassicHeader(SaveWriter w) => w.Str("ObjectProperty");
    public override void WriteValue(SaveWriter w, PropertyFormat f)
    {
        w.I32(Paths.Length);
        foreach (var path in Paths)
            w.Ref(path);
    }
}

public sealed record PByteArray(string Name, byte[] Data) : P(Name)
{
    protected override TypeNode CompleteType => new("ArrayProperty", new TypeNode("ByteProperty"));
    protected override void WriteClassicHeader(SaveWriter w) => w.Str("ByteProperty");
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.I32(Data.Length).Bytes(Data);
}

public sealed record PIntArray(string Name, params int[] Values) : P(Name)
{
    protected override TypeNode CompleteType => new("ArrayProperty", new TypeNode("IntProperty"));
    protected override void WriteClassicHeader(SaveWriter w) => w.Str("IntProperty");
    public override void WriteValue(SaveWriter w, PropertyFormat f)
    {
        w.I32(Values.Length);
        foreach (int v in Values)
            w.I32(v);
    }
}

/// <summary>Array of property-list structs, including the classic format's inner tag.</summary>
public sealed record PStructArray(string Name, string StructType, params P[][] Items) : P(Name)
{
    protected override TypeNode CompleteType => new("ArrayProperty", new TypeNode("StructProperty", new TypeNode(StructType, new TypeNode("/Script/FactoryGame"))));
    protected override void WriteClassicHeader(SaveWriter w) => w.Str("StructProperty");

    public override void WriteValue(SaveWriter w, PropertyFormat f)
    {
        w.I32(Items.Length);
        byte[] elements = SaveWriter.Build(e =>
        {
            foreach (var item in Items)
                WriteList(e, item, f);
        });
        if (!f.Complete)
            w.Str(Name).Str("StructProperty").I32(elements.Length).I32(0).Str(StructType).Zeros(16).Byte(0);
        w.Bytes(elements);
    }
}

public sealed record PVectorSet(string Name, params (double X, double Y, double Z)[] Values) : P(Name)
{
    protected override TypeNode CompleteType => new("SetProperty", Struct("Vector"));
    protected override void WriteClassicHeader(SaveWriter w) => w.Str("StructProperty");

    public override void WriteValue(SaveWriter w, PropertyFormat f)
    {
        w.I32(0).I32(Values.Length);
        foreach (var (x, y, z) in Values)
            w.Vector(x, y, z, doubles: false);
    }
}

public sealed record PStrIntMap(string Name, params (string Key, int Value)[] Entries) : P(Name)
{
    protected override TypeNode CompleteType => new("MapProperty", new TypeNode("StrProperty"), new TypeNode("IntProperty"));
    protected override void WriteClassicHeader(SaveWriter w) => w.Str("StrProperty").Str("IntProperty");

    public override void WriteValue(SaveWriter w, PropertyFormat f)
    {
        w.I32(0).I32(Entries.Length);
        foreach (var (key, value) in Entries)
            w.Str(key).I32(value);
    }
}

/// <summary>Writes arbitrary bytes as the payload of a declared type (for corruption tests).</summary>
public sealed record PRaw(string Name, string Type, byte[] Payload) : P(Name)
{
    protected override TypeNode CompleteType => new(Type);
    public override void WriteValue(SaveWriter w, PropertyFormat f) => w.Bytes(Payload);
}
