using System.Buffers.Binary;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using SkiaSharp;

namespace SatisfactoryMapRenderer.Game;

/// <summary>One mesh of the model library: where its vertices and indices are in models.bin.</summary>
/// <param name="V">
/// Vertex count. A vertex is <see cref="GameModels.VertexBytes"/> bytes: position as 3 × int16 (to be scaled into the
/// bounds), normal as 3 × int8, then (at byte 12) the texture coordinate as 2 × float32.
/// </param>
/// <param name="I">Index count; indices are uint16, or uint32 if <paramref name="Wide"/>.</param>
/// <param name="Min">Lower corner of the bounds (cm).</param>
/// <param name="Size">Extent of the bounds (cm); a position p is Min + (p + 32768) / 65535 × Size.</param>
/// <param name="Sections">Runs of indices with one material each: first index, index count, material.</param>
public sealed record ModelMesh(int V, int I, bool Wide, long VertexOffset, long IndexOffset, double[] Min, double[] Size, int[][] Sections);

/// <summary>How a surface is drawn.</summary>
/// <param name="Kind">
/// "factory": the game's shared factory material, nine surface types picked by the texture coordinate;
/// "texture": an image (<paramref name="Texture"/>, in the textures folder), "texture-mirrored": the same,
/// repeated mirrored; "flat": one colour.
/// </param>
/// <param name="Root">The master material at the top of the parent chain; tells the families of materials apart.</param>
public sealed record ModelMaterial(string Name, string Kind, string? Texture, string? Color, string? Root = null);

/// <summary>A mesh placed inside a building: translation (cm), rotation (quaternion x y z w) and scale, in the building's own space.</summary>
public sealed record ModelPart(string Mesh, double[] T, double[] R, double[] S);

/// <summary>Index of the model library (models.json next to models.bin).</summary>
public sealed class ModelLibrary
{
    public const string IndexFile = "models.json", DataFile = "models.bin";

    /// <summary>Raised when the layout of models.bin or the meaning of the index changes.</summary>
    public const int FormatVersion = 2;
    public const string TextureDirectory = "textures";

    public int Version { get; set; }
    public Dictionary<string, ModelMesh> Meshes { get; set; } = [];
    public List<ModelMaterial> Materials { get; set; } = [];

    /// <summary>File names (in the textures folder) of the nine layers of the shared factory material.</summary>
    public List<string> FactoryLayers { get; set; } = [];

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
            var library = JsonSerializer.Deserialize<ModelLibrary>(File.ReadAllText(index), Json);
            return library?.Version == FormatVersion ? library : null;
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
/// simplified level of detail, the meshes themselves with their texture coordinates, and for every surface
/// its material: either the game's shared factory material or a texture image, exported in a reduced size.
/// </summary>
public static class GameModels
{
    /// <summary>The level of detail with the fewest triangles that still has this many is used.</summary>
    private const int WantedTriangles = 250;

    /// <summary>Helper geometry that is not part of what a building looks like.</summary>
    private static readonly string[] Ignored = ["Fog", "Clearance", "Hologram", "Arrow", "Collision", "Decal", "Snap", "Preview", "Guide", "Glow", "Trigger"];

    private static readonly string[] MeshComponentTypes = ["StaticMeshComponent", "FGColoredInstanceMeshProxy", "InstancedStaticMeshComponent", "FGProductionIndicatorInstanceComponent"];

    public const int VertexBytes = 20;
    private const int TextureSize = 512;
    private const string FactoryMaterial = "MM_Factory_Array", FactoryArray = "TX2D_FactoryBase_BC";

    /// <summary>Materials that are overlays or stand-ins for distant views, not surfaces of the model.</summary>
    private static readonly string[] SkippedMaterials = ["Decal", "_IMP", "Imposter", "FactoryLod", "Hologram"];

    private static readonly string[] AlbedoNames = ["Albedo", "BaseColor", "Base Color", "BC", "Diffuse", "Color", "Albedo_A", "MainTexture", "Texture"];
    private static readonly string[] AlbedoSuffixes = ["_BC", "_Alb", "_Albedo", "_BaseColor", "_D", "_Diff", "_Diffuse", "_Color"];

    public static ModelLibrary Extract(GameArchive archive, string directory, Action<string> log)
    {
        var library = new ModelLibrary { Version = ModelLibrary.FormatVersion };
        string textureDirectory = Path.Combine(directory, ModelLibrary.TextureDirectory);
        if (Directory.Exists(textureDirectory))
            Directory.Delete(textureDirectory, recursive: true);
        Directory.CreateDirectory(textureDirectory);
        var materials = new MaterialTable(library, textureDirectory);
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
                    if (Write(mesh, stream, materials) is { } written)
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
            $"{library.Meshes.Values.Sum(m => (long)m.I) / 3:N0} triangles, {new FileInfo(Path.Combine(directory, ModelLibrary.DataFile)).Length / 1e6:0.0} MB; " +
            $"{library.Materials.Count:N0} materials ({library.Materials.Count(m => m.Kind == "factory")} factory, {library.Materials.Count(m => m.Kind.StartsWith("texture", StringComparison.Ordinal))} textured), " +
            $"{Directory.GetFiles(textureDirectory).Length} textures, {Directory.GetFiles(textureDirectory).Sum(f => new FileInfo(f).Length) / 1e6:0.0} MB");
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

    private static ModelMesh? Write(UStaticMesh mesh, Stream stream, MaterialTable materials)
    {
        var lods = mesh.RenderData?.LODs?.Where(l => l is { SkipLod: false, PositionVertexBuffer.Verts.Length: > 0 } && l.IndexBuffer is { Length: >= 3 }).ToList();
        if (lods is not { Count: > 0 })
            return null;

        // Coarse, but not so coarse that the shape is lost, and not one of the levels that swap the real
        // materials for a stand-in.
        int[] Material(FStaticMeshLODResources lod) => lod.Sections.Select(s => materials.Index(mesh, s.MaterialIndex)).ToArray();
        var candidates = lods.Select(l => (Lod: l, Materials: Material(l))).ToList();
        var real = candidates.Where(c => c.Materials.Any(m => m >= 0)).ToList();
        if (real.Count == 0)
            return null;
        var (lod, sectionMaterials) = real.Where(c => c.Lod.IndexBuffer!.Length / 3 >= WantedTriangles).OrderBy(c => c.Lod.IndexBuffer!.Length).FirstOrDefault() is { Lod: not null } wanted
            ? wanted
            : real.OrderByDescending(c => c.Lod.IndexBuffer!.Length).First();

        var positions = lod.PositionVertexBuffer!.Verts;
        var extra = lod.VertexBuffer?.UV;
        var indices = lod.IndexBuffer!;
        int vertexCount = positions.Length;

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
        byte[] vertex = new byte[VertexBytes];
        for (int i = 0; i < vertexCount; i++)
        {
            var p = positions[i];
            BinaryPrimitives.WriteInt16LittleEndian(vertex, Quantize((p.X - minX) / sizeX));
            BinaryPrimitives.WriteInt16LittleEndian(vertex.AsSpan(2), Quantize((p.Y - minY) / sizeY));
            BinaryPrimitives.WriteInt16LittleEndian(vertex.AsSpan(4), Quantize((p.Z - minZ) / sizeZ));
            var item = extra is not null && i < extra.Length ? extra[i] : null;
            FVector n = item?.Normal is { Length: > 2 } packed ? (FVector)packed[2] : new FVector(0, 0, 1);
            vertex[6] = (byte)(sbyte)Math.Clamp(Math.Round(n.X * 127), -127, 127);
            vertex[7] = (byte)(sbyte)Math.Clamp(Math.Round(n.Y * 127), -127, 127);
            vertex[8] = (byte)(sbyte)Math.Clamp(Math.Round(n.Z * 127), -127, 127);
            float u = item?.UV is { Length: > 0 } uv ? uv[0].U : 0, v = item?.UV is { Length: > 0 } uv2 ? uv2[0].V : 0;
            BinaryPrimitives.WriteSingleLittleEndian(vertex.AsSpan(12), float.IsFinite(u) ? u : 0);
            BinaryPrimitives.WriteSingleLittleEndian(vertex.AsSpan(16), float.IsFinite(v) ? v : 0);
            stream.Write(vertex);
        }

        // Only the sections with a real material are written, one after the other.
        Align(stream);
        long indexOffset = stream.Position;
        bool wide = vertexCount > 65535;
        byte[] index = new byte[4];
        var sections = new List<int[]>();
        int written = 0;
        for (int s = 0; s < lod.Sections.Length; s++)
        {
            var section = lod.Sections[s];
            if (sectionMaterials[s] < 0 || section.NumTriangles <= 0)
                continue;
            int count = Math.Min(section.NumTriangles * 3, indices.Length - section.FirstIndex);
            count -= count % 3;
            if (count <= 0)
                continue;
            for (int i = 0; i < count; i++)
            {
                int value = indices[section.FirstIndex + i];
                if (wide)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(index, (uint)value);
                    stream.Write(index, 0, 4);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(index, (ushort)value);
                    stream.Write(index, 0, 2);
                }
            }
            sections.Add([written, count, sectionMaterials[s]]);
            written += count;
        }
        if (written == 0)
            return null;
        return new ModelMesh(vertexCount, written, wide, vertexOffset, indexOffset,
            [Math.Round(minX, 2), Math.Round(minY, 2), Math.Round(minZ, 2)], [Math.Round(sizeX, 2), Math.Round(sizeY, 2), Math.Round(sizeZ, 2)], [.. sections]);
    }

    /// <summary>Materials of the library, each analysed once; their textures are written as they are found.</summary>
    private sealed class MaterialTable(ModelLibrary library, string textureDirectory)
    {
        private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _textures = new(StringComparer.Ordinal);

        /// <summary>Index into <see cref="ModelLibrary.Materials"/>, or -1 for a surface that is not drawn.</summary>
        public int Index(UStaticMesh mesh, int slot)
        {
            if (mesh.Materials is not { } slots || slot < 0 || slot >= slots.Length || slots[slot]?.ResolvedObject is not { } resolved)
                return -1;
            string name = resolved.Name.Text;
            if (_byName.TryGetValue(name, out int known))
                return known;
            int index = -1;
            if (!SkippedMaterials.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (Describe(name, slots[slot]!.Load()) is { } material)
                    {
                        index = library.Materials.Count;
                        library.Materials.Add(material);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // An unreadable material: the surface is left out.
                }
            }
            return _byName[name] = index;
        }

        private ModelMaterial? Describe(string name, UObject? material)
        {
            // From the material itself up through its parents; what a child sets wins over its parent.
            var chain = new List<UObject>();
            for (var current = material; current is not null && chain.Count < 8; current = (current as UMaterialInstance)?.Parent)
                chain.Add(current);
            if (chain.Count == 0)
                return null;
            if (chain.Any(c => SkippedMaterials.Any(s => c.Name.Contains(s, StringComparison.OrdinalIgnoreCase))))
                return null;

            if (chain.Any(c => c.Name == FactoryMaterial))
            {
                if (library.FactoryLayers.Count == 0 && chain.Last() is UMaterial master)
                {
                    var array = master.ReferencedTextures?.FirstOrDefault(t => t?.Name == FactoryArray);
                    if (array is not null)
                    {
                        var layers = GameArchive.DecodeLayers(array, 256);
                        for (int i = 0; i < layers.Count; i++)
                        {
                            string file = $"factory-{i}.jpg";
                            using (var stream = File.Create(Path.Combine(textureDirectory, file)))
                                layers[i].Encode(stream, SKEncodedImageFormat.Jpeg, 88);
                            layers[i].Dispose();
                            library.FactoryLayers.Add(file);
                        }
                    }
                }
                return library.FactoryLayers.Count == 9 ? new ModelMaterial(name, "factory", null, null, FactoryMaterial) : new ModelMaterial(name, "flat", null, "#9aa0a6", FactoryMaterial);
            }

            foreach (var instance in chain.OfType<UMaterialInstanceConstant>())
            {
                var parameters = instance.TextureParameterValues ?? [];
                var albedo = parameters.FirstOrDefault(p => AlbedoNames.Contains(p.ParameterInfo.Name.Text, StringComparer.OrdinalIgnoreCase))
                             ?? parameters.FirstOrDefault(p => p.ParameterValue.ResolvedObject is { } t && AlbedoSuffixes.Any(s => t.Name.Text.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
                if (albedo?.ParameterValue.ResolvedObject is { } texture && Texture(texture.Name.Text, albedo.ParameterValue) is { } file)
                {
                    // Foundations hold a quarter of their top in the texture and lay it out mirrored around the middle.
                    bool mirrored = chain.Last().Name.StartsWith("MM_BakedStencil", StringComparison.Ordinal);
                    return new ModelMaterial(name, mirrored ? "texture-mirrored" : "texture", file, null, chain.Last().Name);
                }
            }
            if (chain.Last() is UMaterial root)
            {
                var texture = root.ReferencedTextures?.FirstOrDefault(t => t is UTexture2D && AlbedoSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
                if (texture is not null && Texture(texture.Name, texture) is { } file)
                    return new ModelMaterial(name, "texture", file, null, chain.Last().Name);
            }

            string color = name.Contains("Glass", StringComparison.OrdinalIgnoreCase) ? "#9fc4d8"
                : name.Contains("Concrete", StringComparison.OrdinalIgnoreCase) ? "#a9a9a4"
                : name.Contains("Rubber", StringComparison.OrdinalIgnoreCase) ? "#2b2b2d"
                : "#9aa0a6";
            return new ModelMaterial(name, "flat", null, color, chain.Last().Name);
        }

        private string? Texture(string name, FPackageIndex index) =>
            _textures.TryGetValue(name, out var known) ? known : _textures[name] = Save(name, index.Load() as UTexture);

        private string? Texture(string name, UTexture texture) =>
            _textures.TryGetValue(name, out var known) ? known : _textures[name] = Save(name, texture);

        private string? Save(string name, UTexture? texture)
        {
            if (texture is null)
                return null;
            using var bitmap = GameArchive.Decode(texture, TextureSize);
            if (bitmap is null)
                return null;
            string file = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_')) + ".jpg";
            // JPEG has no transparency: flatten what the texture's alpha channel would hide.
            using var opaque = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(opaque))
            {
                canvas.Clear(SKColors.Gray);
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                using var withoutAlpha = bitmap.Copy(SKColorType.Rgb888x) ?? bitmap.Copy();
                canvas.DrawBitmap(withoutAlpha, 0, 0, paint);
            }
            using var stream = File.Create(Path.Combine(textureDirectory, file));
            opaque.Encode(stream, SKEncodedImageFormat.Jpeg, 85);
            return file;
        }
    }

    private static short Quantize(double unit) => (short)Math.Clamp(Math.Round(unit * 65535 - 32768), short.MinValue, short.MaxValue);

    private static void Align(Stream stream)
    {
        while (stream.Position % 4 != 0)
            stream.WriteByte(0);
    }
}
