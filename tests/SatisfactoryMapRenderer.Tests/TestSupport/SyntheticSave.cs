using System.IO.Compression;

namespace SatisfactoryMapRenderer.Tests.TestSupport;

public sealed record TestObject
{
    public required string Class { get; init; }
    public required string Instance { get; init; }
    public bool IsActor { get; init; } = true;
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double YawDegrees { get; init; }
    public string ParentActor { get; init; } = "";
    public string[] Components { get; init; } = [];
    public P[] Properties { get; init; } = [];

    /// <summary>Class specific bytes written after the property list.</summary>
    public Func<SyntheticSave, byte[]>? Special { get; init; }
}

/// <summary>Builds a complete, compressed .sav file for any supported save version.</summary>
public sealed class SyntheticSave(int headerVersion, int saveVersion)
{
    public const int UE5PackageVersion = 1014; // UE 5.5: complete property type names

    public int HeaderVersion { get; } = headerVersion;
    public int SaveVersion { get; } = saveVersion;
    public string SessionName { get; init; } = "Synthetic Session";
    public string SublevelName { get; init; } = "Level /Game/FactoryGame/Map/GameLevel01/Tile_X1_Y1.Tile_X1_Y1:PersistentLevel";
    public List<TestObject> Persistent { get; } = [];
    public List<TestObject> Sublevel { get; } = [];
    public List<string> Collected { get; } = [];

    public bool IsLegacy => SaveVersion < 29;
    public bool Uses64BitBlobs => SaveVersion >= 37;
    public PropertyFormat Format => new(Complete: SaveVersion >= 53, Doubles: SaveVersion >= 41);

    public byte[] Build()
    {
        var file = new SaveWriter();
        WriteHeader(file);
        byte[] body = BuildBody();
        bool v2 = SaveVersion >= 37;
        for (int offset = 0; offset < body.Length; offset += 131072)
        {
            int length = Math.Min(131072, body.Length - offset);
            byte[] compressed = Compress(body.AsSpan(offset, length));
            file.U32(0x9E2A83C1).U32(v2 ? 0x22222222u : 0u).I64(131072);
            if (v2)
                file.Byte(3);
            file.I64(compressed.Length).I64(length).I64(compressed.Length).I64(length);
            file.Bytes(compressed);
        }
        return file.ToArray();
    }

    public string WriteToTempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smr-test-{Guid.NewGuid():N}.sav");
        File.WriteAllBytes(path, Build());
        return path;
    }

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest))
            zlib.Write(data);
        return output.ToArray();
    }

    private void WriteHeader(SaveWriter w)
    {
        w.I32(HeaderVersion).I32(SaveVersion).I32(123456);
        if (HeaderVersion >= 14)
            w.Str("Synthetic Save");
        w.Str("Persistent_Level").Str("?startloc=Grass Fields").Str(SessionName);
        w.I32(3 * 3600 + 25 * 60);
        w.I64(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks);
        w.Byte(0);
        if (HeaderVersion >= 7)
            w.I32(40);
        if (HeaderVersion >= 8)
            w.Str("").I32(0);
        if (HeaderVersion >= 10)
            w.Str("synthetic-id");
        if (HeaderVersion >= 11)
            w.I32(1);
        if (HeaderVersion >= 12)
            w.I32(0);
        if (HeaderVersion >= 13)
            w.I32(0);
    }

    private byte[] BuildBody()
    {
        var w = new SaveWriter();
        if (Uses64BitBlobs)
            w.I64(0);
        else
            w.I32(0);

        if (SaveVersion >= 53)
            WriteObjectVersionData(w);
        if (SaveVersion >= 38)
            w.I32(1).Str("MainGrid").I32(12800).U32(5).I32(1).Str("Cell_1").U32(9);

        if (IsLegacy)
        {
            var all = Persistent.Concat(Sublevel).ToList();
            w.I32(all.Count);
            foreach (var o in all)
                WriteObjectHeader(w, o);
            w.I32(all.Count);
            foreach (var o in all)
            {
                byte[] data = ObjectData(o);
                w.I32(data.Length).Bytes(data);
            }
            WriteReferences(w, Collected);
        }
        else
        {
            w.I32(1);
            w.Str(SublevelName);
            WriteLevel(w, Sublevel, persistent: false);
            WriteLevel(w, Persistent, persistent: true);
            w.I32(0); // unresolved world save data
        }

        byte[] body = w.ToArray();
        int headerSize = Uses64BitBlobs ? 8 : 4;
        var fixedBody = new SaveWriter();
        fixedBody.Bytes(body);
        if (Uses64BitBlobs)
            fixedBody.PatchI64(0, body.Length - headerSize);
        else
            fixedBody.PatchI32(0, body.Length - headerSize);
        return fixedBody.ToArray();
    }

    private static void WriteObjectVersionData(SaveWriter w) =>
        w.U32(1).I32(522).I32(UE5PackageVersion).I32(0).U16(5).U16(5).U16(0).U32(0).Str("++UE5+Release-5.5").I32(0);

    private void WriteLevel(SaveWriter w, List<TestObject> objects, bool persistent)
    {
        int tocSizePosition = w.Position;
        w.I32(0);
        if (Uses64BitBlobs)
            w.I32(0);
        int tocStart = w.Position;
        w.I32(objects.Count);
        foreach (var o in objects)
            WriteObjectHeader(w, o);
        w.PatchI32(tocSizePosition, w.Position - tocStart);

        int dataSizePosition = w.Position;
        w.I32(0);
        if (Uses64BitBlobs)
            w.I32(0);
        int dataStart = w.Position;
        w.I32(objects.Count);
        foreach (var o in objects)
        {
            if (SaveVersion >= 38)
                w.I32(SaveVersion).I32(0);
            byte[] data = ObjectData(o);
            w.I32(data.Length).Bytes(data);
            if (SaveVersion >= 53)
                w.I32(0);
        }
        w.PatchI32(dataSizePosition, w.Position - dataStart);

        if (!persistent && SaveVersion >= 51)
            w.I32(SaveVersion);

        if (persistent)
        {
            if (SaveVersion >= 46)
            {
                w.I32(1).Str(SublevelName);
                WriteReferences(w, Collected);
            }
            else
            {
                WriteReferences(w, []);
            }
        }
        else
        {
            WriteReferences(w, SaveVersion >= 46 ? [] : Collected);
            if (SaveVersion >= 53)
                w.I32(0);
        }
    }

    private static void WriteReferences(SaveWriter w, List<string> paths)
    {
        w.I32(paths.Count);
        foreach (var path in paths)
            w.Ref(path, path[..path.IndexOf(':')]);
    }

    private void WriteObjectHeader(SaveWriter w, TestObject o)
    {
        w.I32(o.IsActor ? 1 : 0).Str(o.Class).Str("Persistent_Level").Str(o.Instance);
        if (SaveVersion >= 49)
            w.U32(0);
        if (o.IsActor)
        {
            double half = o.YawDegrees * Math.PI / 360;
            w.I32(1);
            w.F32(0).F32(0).F32((float)Math.Sin(half)).F32((float)Math.Cos(half));
            w.F32((float)o.X).F32((float)o.Y).F32((float)o.Z);
            w.F32(1).F32(1).F32(1);
            w.I32(0);
        }
        else
        {
            w.Str(o.ParentActor);
        }
    }

    private byte[] ObjectData(TestObject o) => SaveWriter.Build(w =>
    {
        if (o.IsActor)
        {
            w.Str("").Str("");
            w.I32(o.Components.Length);
            foreach (var component in o.Components)
                w.Ref(component);
        }
        if (Format.Complete)
            w.Byte(0);
        P.WriteList(w, o.Properties, Format);
        w.I32(0); // no object guid
        if (o.Special is not null)
            w.Bytes(o.Special(this));
    });

    // ------------------------------------------------------------------ special data helpers

    public static byte[] Wire(string source, string target) => SaveWriter.Build(w => w.Ref(source).Ref(target));

    public byte[] LightweightBuildables(string className, params (double X, double Y, double YawDegrees)[] instances) => SaveWriter.Build(w =>
    {
        // Saves before v48 do not store the runtime version; they use the initial instance layout.
        int runtimeVersion = SaveVersion >= 48 ? 4 : 1;
        if (SaveVersion >= 48)
            w.I32(runtimeVersion);
        w.I32(1);
        w.Ref(className, "");
        w.I32(instances.Length);
        foreach (var (x, y, yaw) in instances)
        {
            double half = yaw * Math.PI / 360;
            w.F64(0).F64(0).F64(Math.Sin(half)).F64(Math.Cos(half));
            w.F64(x).F64(y).F64(0);
            w.F64(1).F64(1).F64(1);
            for (int i = 0; i < 4; i++)
                w.Str("").Str(""); // swatch, material, pattern, skin
            w.F32(1).F32(0.5f).F32(0).F32(1).F32(0).F32(0).F32(0).F32(1);
            w.Str("").Str("");     // paint finish
            w.Byte(0);             // pattern rotation
            w.Ref("/Game/FactoryGame/Recipes/Buildings/Foundations/Recipe_Foundation_8x1_01.Recipe_Foundation_8x1_01_C", "");
            w.Str("").Str("");     // blueprint proxy
            if (runtimeVersion >= 2)
                w.I32(0);          // no type specific data
            if (runtimeVersion >= 3)
            {
                w.Byte(0);         // built by: service provider
                if (SaveVersion >= 57)
                    w.I32(0);
                else
                    w.Byte(0);
            }
        }
    });
}
