using System.Buffers.Binary;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace SatisfactoryMapRenderer.Game;

/// <summary>One mesh of the model library: where its vertices and indices are in models.bin.</summary>
/// <param name="V">Vertex count. A vertex is 10 bytes: position as 3 × int16 (to be scaled into the bounds), normal as 3 × int8, 1 unused.</param>
/// <param name="I">Index count; indices are uint16, or uint32 if <paramref name="Wide"/>.</param>
/// <param name="Min">Lower corner of the bounds (cm).</param>
/// <param name="Size">Extent of the bounds (cm); a position p is Min + (p + 32768) / 65535 × Size.</param>
public sealed record ModelMesh(int V, int I, bool Wide, long VertexOffset, long IndexOffset, double[] Min, double[] Size);

/// <summary>A mesh placed inside a building: translation (cm), rotation (quaternion x y z w) and scale, in the building's own space.</summary>
public sealed record ModelPart(string Mesh, double[] T, double[] R, double[] S);

/// <summary>Index of the model library (models.json next to models.bin).</summary>
public sealed class ModelLibrary
{
    public const string IndexFile = "models.json", DataFile = "models.bin";

    public Dictionary<string, ModelMesh> Meshes { get; set; } = [];

    /// <summary>Building class ("Build_ConstructorMk1_C") → the meshes it consists of.</summary>
    public Dictionary<string, List<ModelPart>> Classes { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ModelLibrary? TryLoad(string directory)
    {
        string index = Path.Combine(directory, IndexFile);
        if (!File.Exists(index) || !File.Exists(Path.Combine(directory, DataFile)))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ModelLibrary>(File.ReadAllText(index), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string directory) => File.WriteAllText(Path.Combine(directory, IndexFile), JsonSerializer.Serialize(this, Json));

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>
/// Reads the 3D models of all buildables from the game: which static meshes a building consists of and, in a
/// simplified level of detail, the meshes themselves. Materials and textures are not read; the 3D view shows
/// the shapes in the colours of the map.
/// </summary>
public static class GameModels
{
    /// <summary>The level of detail with the fewest triangles that still has this many is used.</summary>
    private const int WantedTriangles = 250;

    /// <summary>Helper geometry that is not part of what a building looks like.</summary>
    private static readonly string[] Ignored = ["Fog", "Clearance", "Hologram", "Arrow", "Collision", "Decal", "Snap", "Preview", "Guide", "Glow", "Trigger"];

    private static readonly string[] MeshComponentTypes = ["StaticMeshComponent", "FGColoredInstanceMeshProxy", "InstancedStaticMeshComponent", "FGProductionIndicatorInstanceComponent"];

    public static ModelLibrary Extract(GameArchive archive, string directory, Action<string> log)
    {
        var library = new ModelLibrary();
        var meshObjects = new Dictionary<string, UStaticMesh>(StringComparer.Ordinal);

        foreach (string file in archive.Files.Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                                                         && Path.GetFileName(f).StartsWith("Build_", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var exports = archive.LoadExports(file).ToList();
                var parts = new List<ModelPart>();

                // Most buildings list their meshes as "abstract instances" on the class defaults.
                var defaults = exports.FirstOrDefault(e => e.Name == "Default__" + name + "_C");
                if (defaults is not null && defaults.TryGetValue(out FPackageIndex instanceData, "mInstanceDataCDO") && !instanceData.IsNull
                    && instanceData.Load() is { } data && data.TryGetValue(out FStructFallback[] instances, "Instances"))
                {
                    foreach (var instance in instances)
                    {
                        if (!instance.TryGetValue(out FPackageIndex meshIndex, "StaticMesh") || Mesh(meshIndex, meshObjects) is not { } mesh)
                            continue;
                        var t = instance.GetOrDefault("RelativeTransform", FTransform.Identity);
                        parts.Add(Part(mesh, t.Translation, t.Rotation, t.Scale3D));
                    }
                }

                // The others (machines with moving parts) have ordinary mesh components.
                if (parts.Count == 0)
                {
                    foreach (var component in exports.Where(e => MeshComponentTypes.Contains(e.ExportType)))
                    {
                        if (Ignored.Any(i => component.Name.Contains(i, StringComparison.OrdinalIgnoreCase))
                            || (component.TryGetValue(out bool visible, "bVisible") && !visible)
                            || (component.TryGetValue(out bool hidden, "bHiddenInGame") && hidden)
                            || !component.TryGetValue(out FPackageIndex meshIndex, "StaticMesh") || Mesh(meshIndex, meshObjects) is not { } mesh)
                            continue;
                        var location = component.GetOrDefault("RelativeLocation", new FVector(0, 0, 0));
                        var rotation = component.GetOrDefault("RelativeRotation", new FRotator(0, 0, 0)).Quaternion();
                        var scale = component.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1));
                        parts.Add(Part(mesh, location, rotation, scale));
                    }
                }

                if (parts.Count > 0)
                    library.Classes[name + "_C"] = parts;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A building that cannot be read is drawn as a box.
            }
        }

        // The mesh data itself, each mesh once.
        using (var stream = File.Create(Path.Combine(directory, ModelLibrary.DataFile)))
        {
            foreach (var (name, mesh) in meshObjects.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                try
                {
                    if (Write(mesh, stream) is { } written)
                        library.Meshes[name] = written;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Left out; its buildings fall back to boxes for this part.
                }
            }
        }

        // Parts whose mesh could not be written are dropped, and classes left without parts with them.
        foreach (var (name, parts) in library.Classes.ToList())
        {
            parts.RemoveAll(p => !library.Meshes.ContainsKey(p.Mesh));
            if (parts.Count == 0)
                library.Classes.Remove(name);
        }
        library.Save(directory);
        log($"  3D models: {library.Classes.Count:N0} buildings from {library.Meshes.Count:N0} meshes, " +
            $"{library.Meshes.Values.Sum(m => (long)m.I) / 3:N0} triangles, {new FileInfo(Path.Combine(directory, ModelLibrary.DataFile)).Length / 1e6:0.0} MB");
        return library;
    }

    private static string? Mesh(FPackageIndex index, Dictionary<string, UStaticMesh> meshes)
    {
        if (index.IsNull || index.ResolvedObject is not { } resolved)
            return null;
        string name = resolved.Name.Text;
        if (Ignored.Any(i => name.Contains(i, StringComparison.OrdinalIgnoreCase)))
            return null;
        if (!meshes.ContainsKey(name))
        {
            if (index.Load() is not UStaticMesh mesh)
                return null;
            meshes[name] = mesh;
        }
        return name;
    }

    private static ModelPart Part(string mesh, FVector t, FQuat r, FVector s) =>
        new(mesh, [Round(t.X), Round(t.Y), Round(t.Z)], [Math.Round(r.X, 5), Math.Round(r.Y, 5), Math.Round(r.Z, 5), Math.Round(r.W, 5)], [Math.Round(s.X, 4), Math.Round(s.Y, 4), Math.Round(s.Z, 4)]);

    private static double Round(double v) => Math.Round(v, 1);

    private static ModelMesh? Write(UStaticMesh mesh, Stream stream)
    {
        var lods = mesh.RenderData?.LODs?.Where(l => l is { SkipLod: false, PositionVertexBuffer.Verts.Length: > 0 } && l.IndexBuffer is { Length: >= 3 }).ToList();
        if (lods is not { Count: > 0 })
            return null;
        // Coarse, but not so coarse that the shape is lost.
        var lod = lods.Where(l => l.IndexBuffer!.Length / 3 >= WantedTriangles).OrderBy(l => l.IndexBuffer!.Length).FirstOrDefault()
                  ?? lods.OrderByDescending(l => l.IndexBuffer!.Length).First();

        var positions = lod.PositionVertexBuffer!.Verts;
        var normals = lod.VertexBuffer?.UV;
        var indices = lod.IndexBuffer!;
        int vertexCount = positions.Length, indexCount = indices.Length - indices.Length % 3;

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in positions)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
        }
        double sizeX = Math.Max(maxX - minX, 0.01), sizeY = Math.Max(maxY - minY, 0.01), sizeZ = Math.Max(maxZ - minZ, 0.01);

        Align(stream);
        long vertexOffset = stream.Position;
        byte[] vertex = new byte[10];
        for (int i = 0; i < vertexCount; i++)
        {
            var p = positions[i];
            BinaryPrimitives.WriteInt16LittleEndian(vertex, Quantize((p.X - minX) / sizeX));
            BinaryPrimitives.WriteInt16LittleEndian(vertex.AsSpan(2), Quantize((p.Y - minY) / sizeY));
            BinaryPrimitives.WriteInt16LittleEndian(vertex.AsSpan(4), Quantize((p.Z - minZ) / sizeZ));
            FVector n = normals is not null && i < normals.Length && normals[i].Normal is { Length: > 2 } packed ? (FVector)packed[2] : new FVector(0, 0, 1);
            vertex[6] = (byte)(sbyte)Math.Clamp(Math.Round(n.X * 127), -127, 127);
            vertex[7] = (byte)(sbyte)Math.Clamp(Math.Round(n.Y * 127), -127, 127);
            vertex[8] = (byte)(sbyte)Math.Clamp(Math.Round(n.Z * 127), -127, 127);
            stream.Write(vertex);
        }

        Align(stream);
        long indexOffset = stream.Position;
        bool wide = vertexCount > 65535;
        byte[] index = new byte[4];
        for (int i = 0; i < indexCount; i++)
        {
            if (wide)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(index, (uint)indices[i]);
                stream.Write(index, 0, 4);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(index, (ushort)indices[i]);
                stream.Write(index, 0, 2);
            }
        }
        return new ModelMesh(vertexCount, indexCount, wide, vertexOffset, indexOffset,
            [Math.Round(minX, 2), Math.Round(minY, 2), Math.Round(minZ, 2)], [Math.Round(sizeX, 2), Math.Round(sizeY, 2), Math.Round(sizeZ, 2)]);
    }

    private static short Quantize(double unit) => (short)Math.Clamp(Math.Round(unit * 65535 - 32768), short.MinValue, short.MaxValue);

    private static void Align(Stream stream)
    {
        while (stream.Position % 4 != 0)
            stream.WriteByte(0);
    }
}
