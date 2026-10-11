using System.Buffers.Binary;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse.UE4.Objects.RenderCore;
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
/// repeated mirrored, "texture-painted": the same, as the detail of a painted surface; "flat": one colour.
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
    public Dictionary<string, List<ModelPart>> Classes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Conveyor lifts: their class has three parts, the piece at the start, the piece that is repeated in
    /// between, and the piece at the other end. The value is the height (cm) of one piece in between.
    /// </summary>
    public Dictionary<string, double> Lifts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Classes that are laid along a path (belts, pipes, hypertubes): their one mesh runs along x and is
    /// repeated, stretched to this length (cm) per piece.
    /// </summary>
    public Dictionary<string, double> Splines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ModelLibrary? TryLoad(string directory)
    {
        string index = Path.Combine(directory, IndexFile);
        if (!File.Exists(index) || !File.Exists(Path.Combine(directory, DataFile)))
            return null;
        try
        {
            var library = JsonSerializer.Deserialize<ModelLibrary>(File.ReadAllText(index), Json);
            if (library?.Version != FormatVersion)
                return null;
            // The game is not consistent in its own spelling (Build_PipelinePumpMk2 in saves, …MK2 in the files).
            library.Classes = new(library.Classes, StringComparer.OrdinalIgnoreCase);
            library.Splines = new(library.Splines, StringComparer.OrdinalIgnoreCase);
            library.Lifts = new(library.Lifts, StringComparer.OrdinalIgnoreCase);
            return library;
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
    private const int WantedTriangles = 250, RepeatedTriangles = 90;

    /// <summary>Helper geometry that is not part of what a building looks like.</summary>
    private static readonly string[] Ignored = ["Fog", "Clearance", "Hologram", "Arrow", "Collision", "Decal", "Snap", "Preview", "Guide", "Glow", "Trigger"];

    private static readonly string[] MeshComponentTypes = ["StaticMeshComponent", "FGColoredInstanceMeshProxy", "InstancedStaticMeshComponent", "FGProductionIndicatorInstanceComponent"];

    public const int VertexBytes = 20;
    private const int TextureSize = 512;
    private const string FactoryMaterial = "MM_Factory_Array", FactoryArray = "TX2D_FactoryBase_BC";

    /// <summary>Materials that are overlays or stand-ins for distant views, not surfaces of the model.</summary>
    /// <summary>Materials whose texture is black or dark where the game puts the paint.</summary>
    private static readonly string[] PaintedMaterials = ["MMU_Pipeline", "MMU_WaterPump"];
    private static readonly string[] StandIns = ["_IMP", "Imposter", "FactoryLod"];
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
        var meshObjects = new Dictionary<string, UObject>(StringComparer.Ordinal);
        var repeated = new HashSet<string>(StringComparer.Ordinal);
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var partKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var liftPieces = new Dictionary<string, string>(StringComparer.Ordinal); // what later lifts take over from Mk.1

        foreach (string file in archive.Files.Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                                                         && Path.GetFileName(f).StartsWith("Build_", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var exports = archive.LoadExports(file).ToList();
                var parts = new List<ModelPart>();
                var keys = new List<string>(); // per part the name of the component it comes from
                if (exports.OfType<UClass>().FirstOrDefault()?.SuperStruct?.Name is { } super && super.StartsWith("Build_", StringComparison.Ordinal))
                    parentOf[name + "_C"] = super;

                var defaults = exports.FirstOrDefault(e => e.Name == "Default__" + name + "_C");
                // Belts, pipes and hypertubes: one piece of mesh, repeated along the path.
                if (defaults is not null && defaults.TryGetValue(out FPackageIndex pieceIndex, "mMesh") && defaults.TryGetValue(out FPackageIndex _, "mSplineComponent")
                    && Mesh(pieceIndex, meshObjects) is { } piece)
                {
                    double length = defaults.GetOrDefault<float>("mMeshLength");
                    if (length <= 0)
                        length = Math.Max((meshObjects[piece] as UStaticMesh)?.RenderData?.Bounds?.BoxExtent.X * 2 ?? 0, 100);
                    library.Classes[name + "_C"] = [Part(piece, new FVector(0, 0, 0), FQuat.Identity, new FVector(1, 1, 1))];
                    library.Splines[name + "_C"] = length;
                    repeated.Add(piece);
                    continue;
                }

                // Conveyor lifts: a piece at each end and one that is stacked in between. The later marks only
                // name what differs from Mk.1.
                if (defaults is not null && name.StartsWith("Build_ConveyorLiftMk", StringComparison.Ordinal))
                {
                    var pieces = new List<string>();
                    foreach (string property in new[] { "mBottomMesh", "mMidMesh", "mTopMesh" })
                    {
                        if (defaults.TryGetValue(out FPackageIndex index, property) && Mesh(index, meshObjects) is { } own)
                            liftPieces[property] = own;
                        else if (!liftPieces.TryGetValue(property, out own!))
                            break;
                        pieces.Add(own);
                        if (name.EndsWith("Mk1", StringComparison.Ordinal))
                            liftPieces[property] = own;
                    }
                    if (pieces.Count == 3)
                    {
                        library.Classes[name + "_C"] = [.. pieces.Select(p => Part(p, new FVector(0, 0, 0), FQuat.Identity, new FVector(1, 1, 1)))];
                        library.Lifts[name + "_C"] = 200;
                        repeated.Add(pieces[1]);
                    }
                    continue;
                }

                // Most buildings list their meshes as "abstract instances" on the class defaults.
                if (defaults is not null && defaults.TryGetValue(out FPackageIndex instanceData, "mInstanceDataCDO") && !instanceData.IsNull
                    && instanceData.Load() is { } data && data.TryGetValue(out FStructFallback[] instances, "Instances"))
                {
                    foreach (var instance in instances)
                    {
                        if (!instance.TryGetValue(out FPackageIndex meshIndex, "StaticMesh") || Mesh(meshIndex, meshObjects) is not { } mesh)
                            continue;
                        var t = instance.GetOrDefault("RelativeTransform", FTransform.Identity);
                        parts.Add(Part(mesh, t.Translation, t.Rotation, t.Scale3D));
                        keys.Add("");
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
                        keys.Add(component.Name.Replace("_GEN_VARIABLE", ""));
                    }
                }

                // Moving parts (pump heads, pistons, arms) are meshes of their own, in the pose they rest in.
                int fixedParts = parts.Count;
                foreach (var component in exports.Where(e => e.ExportType is "FGVertexAnimatedMeshComponent" or "SkeletalMeshComponent"))
                {
                    if (Ignored.Any(i => component.Name.Contains(i, StringComparison.OrdinalIgnoreCase))
                        || (component.TryGetValue(out bool visible, "bVisible") && !visible)
                        || (component.TryGetValue(out bool hidden, "bHiddenInGame") && hidden)
                        || !(component.TryGetValue(out FPackageIndex meshIndex, "StaticMesh") || component.TryGetValue(out meshIndex, "SkeletalMesh") || component.TryGetValue(out meshIndex, "SkinnedAsset"))
                        || Mesh(meshIndex, meshObjects) is not { } mesh
                        || parts.Take(fixedParts).Any(p => p.Mesh == mesh))
                        continue;
                    parts.Add(Part(mesh, component.GetOrDefault("RelativeLocation", new FVector(0, 0, 0)),
                        component.GetOrDefault("RelativeRotation", new FRotator(0, 0, 0)).Quaternion(), component.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1))));
                    keys.Add(component.Name.Replace("_GEN_VARIABLE", ""));
                }

                // The space elevator's shaft and the platform on top of it are stored hanging below the
                // building; the game lifts them when it is built, so that the shaft stands on the base.
                if (name == "Build_SpaceElevator" && parts.FirstOrDefault(p => p.Mesh.Contains("MidPart_01", StringComparison.Ordinal)) is { } shaft
                    && (meshObjects[shaft.Mesh] as UStaticMesh)?.RenderData?.Bounds is { } bounds)
                {
                    double lift = -(bounds.Origin.Z - bounds.BoxExtent.Z);
                    for (int i = 0; i < parts.Count; i++)
                        if (parts[i].Mesh.Contains("MidPart", StringComparison.Ordinal) || parts[i].Mesh.Contains("SpaceElevator_Elevator", StringComparison.Ordinal))
                            parts[i] = parts[i] with { T = [parts[i].T[0], parts[i].T[1], Round(parts[i].T[2] + lift)] };
                }

                if (parts.Count > 0)
                    library.Classes[name + "_C"] = parts;
                partKeys[name + "_C"] = keys;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A building that cannot be read is drawn as a box.
            }
        }

        // A building made from another building (the power tower with a platform from the power tower) only
        // holds what it adds or changes; the rest is the other building's. Buildings that list their meshes
        // as instances list all of them.
        var inherited = new HashSet<string>(StringComparer.Ordinal);
        void Inherit(string name)
        {
            if (!inherited.Add(name) || !parentOf.TryGetValue(name, out string? parent) || library.Splines.ContainsKey(name) || library.Lifts.ContainsKey(name)
                || library.Splines.ContainsKey(parent) || library.Lifts.ContainsKey(parent))
                return;
            Inherit(parent);
            if (!library.Classes.TryGetValue(parent, out var theirs) || !partKeys.TryGetValue(parent, out var theirKeys) || !partKeys.TryGetValue(name, out var keys)
                || keys.Contains(""))
                return;
            if (!library.Classes.TryGetValue(name, out var parts))
                library.Classes[name] = parts = [];
            for (int i = 0; i < theirs.Count && i < theirKeys.Count; i++)
            {
                if ((theirKeys[i].Length > 0 && keys.Contains(theirKeys[i])) || parts.Any(p => p.Mesh == theirs[i].Mesh && p.T.SequenceEqual(theirs[i].T)))
                    continue;
                parts.Add(theirs[i]);
                keys.Add(theirKeys[i]);
            }
        }
        foreach (string name in parentOf.Keys.ToList())
            Inherit(name);

        // The mesh data itself, each mesh once.
        using (var stream = File.Create(Path.Combine(directory, ModelLibrary.DataFile)))
        {
            foreach (var (name, mesh) in meshObjects.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                try
                {
                    // a piece that is repeated thousands of times along belts and pipes may be coarser
                    if (Write(mesh, stream, materials, repeated.Contains(name) ? RepeatedTriangles : WantedTriangles) is { } written)
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
            int before = parts.Count;
            parts.RemoveAll(p => !library.Meshes.ContainsKey(p.Mesh));
            if (parts.Count == 0 || (parts.Count < before && library.Lifts.ContainsKey(name)))
            {
                library.Classes.Remove(name);
                library.Splines.Remove(name);
                library.Lifts.Remove(name);
            }
        }
        library.Save(directory);
        log($"  3D models: {library.Classes.Count:N0} buildings from {library.Meshes.Count:N0} meshes, " +
            $"{library.Meshes.Values.Sum(m => (long)m.I) / 3:N0} triangles, {new FileInfo(Path.Combine(directory, ModelLibrary.DataFile)).Length / 1e6:0.0} MB; " +
            $"{library.Materials.Count:N0} materials ({library.Materials.Count(m => m.Kind == "factory")} factory, {library.Materials.Count(m => m.Kind.StartsWith("texture", StringComparison.Ordinal))} textured), " +
            $"{Directory.GetFiles(textureDirectory).Length} textures, {Directory.GetFiles(textureDirectory).Sum(f => new FileInfo(f).Length) / 1e6:0.0} MB");
        return library;
    }

    private static string? Mesh(FPackageIndex index, Dictionary<string, UObject> meshes)
    {
        if (index.IsNull || index.ResolvedObject is not { } resolved)
            return null;
        string name = resolved.Name.Text;
        if (Ignored.Any(i => name.Contains(i, StringComparison.OrdinalIgnoreCase)))
            return null;
        if (!meshes.ContainsKey(name))
        {
            if (index.Load() is not { } mesh || mesh is not (UStaticMesh or USkeletalMesh))
                return null;
            meshes[name] = mesh;
        }
        return name;
    }

    private static ModelPart Part(string mesh, FVector t, FQuat r, FVector s) =>
        new(mesh, [Round(t.X), Round(t.Y), Round(t.Z)], [Math.Round(r.X, 5), Math.Round(r.Y, 5), Math.Round(r.Z, 5), Math.Round(r.W, 5)], [Math.Round(s.X, 4), Math.Round(s.Y, 4), Math.Round(s.Z, 4)]);

    private static double Round(double v) => Math.Round(v, 1);

    /// <summary>One run of triangles with one material.</summary>
    private readonly record struct Run(int Material, int First, int Triangles);

    /// <summary>One level of detail of a mesh, whichever kind the mesh is.</summary>
    private sealed record Level(int VertexCount, int IndexCount, Run[] Runs, Func<int, FVector> Position, Func<int, FVector> Normal, Func<int, (float U, float V)> Uv, Func<int, int> Index);

    private static List<Level> Levels(UObject mesh)
    {
        var levels = new List<Level>();
        if (mesh is UStaticMesh fixedMesh)
        {
            foreach (var l in fixedMesh.RenderData?.LODs ?? [])
            {
                if (l is not { SkipLod: false, PositionVertexBuffer.Verts.Length: > 0 } || l.IndexBuffer is not { Length: >= 3 } indices)
                    continue;
                var positions = l.PositionVertexBuffer.Verts;
                var extra = l.VertexBuffer?.UV;
                levels.Add(new Level(positions.Length, indices.Length, [.. l.Sections.Select(s => new Run(s.MaterialIndex, s.FirstIndex, s.NumTriangles))],
                    i => positions[i],
                    i => extra is not null && i < extra.Length && extra[i].Normal is { Length: > 2 } packed ? (FVector)packed[2] : new FVector(0, 0, 1),
                    i => extra is not null && i < extra.Length && extra[i].UV is { Length: > 0 } uv ? (uv[0].U, uv[0].V) : (0, 0),
                    i => indices[i]));
            }
        }
        else if (mesh is USkeletalMesh skinned)
        {
            // A machine's arms and other animated parts, in the pose they are modelled in.
            foreach (var l in skinned.LODModels ?? [])
            {
                if (l is not { SkipLod: false, VertexBufferGPUSkin: { } buffer } || l.Indices is not { Length: >= 3 } indices)
                    continue;
                FVector[] positions; FPackedNormal[][] normals; FMeshUVFloat[][] uvs;
                if (buffer.VertsFloat is { Length: > 0 } floats)
                    (positions, normals, uvs) = ([.. floats.Select(v => v.Pos)], [.. floats.Select(v => v.Normal)], [.. floats.Select(v => v.UVs)]);
                else if (buffer.VertsHalf is { Length: > 0 } halves)
                    (positions, normals, uvs) = ([.. halves.Select(v => v.Pos)], [.. halves.Select(v => v.Normal)], [.. halves.Select(v => v.UVs)]);
                else
                    continue;
                levels.Add(new Level(positions.Length, indices.Length, [.. l.Sections.Where(s => !s.bDisabled).Select(s => new Run(s.MaterialIndex, s.BaseIndex, s.NumTriangles))],
                    i => positions[i],
                    i => normals[i] is { Length: > 2 } packed ? (FVector)packed[2] : new FVector(0, 0, 1),
                    i => uvs[i] is { Length: > 0 } uv ? (uv[0].U, uv[0].V) : (0, 0),
                    i => indices[i]));
            }
        }
        return levels;
    }

    private static FPackageIndex?[] Slots(UObject mesh) => mesh switch
    {
        UStaticMesh m => m.Materials ?? [],
        USkeletalMesh m => m.Materials ?? [],
        _ => [],
    };

    private static ModelMesh? Write(UObject mesh, Stream stream, MaterialTable materials, int wantedTriangles)
    {
        var lods = Levels(mesh);
        if (lods.Count == 0)
            return null;
        var slots = Slots(mesh);

        // Coarse, but not so coarse that the shape is lost, and not one of the levels that swap the real
        // materials for a stand-in.
        var candidates = lods.Select(l => (Lod: l, Materials: l.Runs.Select(r => materials.Index(slots, r.Material)).ToArray())).ToList();
        var real = candidates.Where(c => c.Materials.Any(m => m >= 0)).ToList();
        if (real.Count == 0)
            return null;
        // Levels that swap part of the model for a stand-in would leave that part out.
        var whole = real.Where(c => !c.Lod.Runs.Any(r => materials.IsStandIn(slots, r.Material))).ToList();
        if (whole.Count > 0)
            real = whole;
        var (lod, sectionMaterials) = real.Where(c => c.Lod.IndexCount / 3 >= wantedTriangles).OrderBy(c => c.Lod.IndexCount).FirstOrDefault() is { Lod: not null } wanted
            ? wanted
            : real.OrderByDescending(c => c.Lod.IndexCount).First();

        int vertexCount = lod.VertexCount;
        var positions = Enumerable.Range(0, vertexCount).Select(lod.Position).ToArray();

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
            FVector n = lod.Normal(i);
            vertex[6] = (byte)(sbyte)Math.Clamp(Math.Round(n.X * 127), -127, 127);
            vertex[7] = (byte)(sbyte)Math.Clamp(Math.Round(n.Y * 127), -127, 127);
            vertex[8] = (byte)(sbyte)Math.Clamp(Math.Round(n.Z * 127), -127, 127);
            var (u, v) = lod.Uv(i);
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
        for (int s = 0; s < lod.Runs.Length; s++)
        {
            var section = lod.Runs[s];
            if (sectionMaterials[s] < 0 || section.Triangles <= 0)
                continue;
            int count = Math.Min(section.Triangles * 3, lod.IndexCount - section.First);
            count -= count % 3;
            if (count <= 0)
                continue;
            for (int i = 0; i < count; i++)
            {
                int value = lod.Index(section.First + i);
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

        /// <summary>A material that replaces the real ones on a coarse level of detail.</summary>
        public bool IsStandIn(FPackageIndex?[] slots, int slot) =>
            slot >= 0 && slot < slots.Length && slots[slot]?.ResolvedObject is { } resolved
            && StandIns.Any(s => resolved.Name.Text.Contains(s, StringComparison.OrdinalIgnoreCase));

        /// <summary>Index into <see cref="ModelLibrary.Materials"/>, or -1 for a surface that is not drawn.</summary>
        public int Index(FPackageIndex?[] slots, int slot)
        {
            if (slot < 0 || slot >= slots.Length || slots[slot]?.ResolvedObject is not { } resolved)
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
                    // Painted surfaces: the texture is dark where the paint goes and shows the bare metal elsewhere.
                    bool painted = PaintedMaterials.Contains(chain.Last().Name);
                    return new ModelMaterial(name, mirrored ? "texture-mirrored" : painted ? "texture-painted" : "texture", file, null, chain.Last().Name);
                }
            }
            if (chain.Last() is UMaterial root)
            {
                var texture = root.ReferencedTextures?.FirstOrDefault(t => t is UTexture2D && AlbedoSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
                if (texture is not null && Texture(texture.Name, texture) is { } file)
                    return new ModelMaterial(name, PaintedMaterials.Contains(root.Name) ? "texture-painted" : "texture", file, null, chain.Last().Name);
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
