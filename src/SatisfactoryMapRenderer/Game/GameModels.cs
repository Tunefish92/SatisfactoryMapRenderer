using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// <param name="Mask">
/// For a texture: a second image that says where the paint goes, green for the first colour and blue for
/// the second (red is shading). Null if the surface is not painted.
/// </param>
public sealed record ModelMaterial(string Name, string Kind, string? Texture, string? Color, string? Root = null, string? Mask = null)
{
    /// <summary>Grey image of where the surface glows, and the colour it glows in; null where it does not.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Glow { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GlowColor { get; init; }
}

/// <summary>A mesh placed inside a building: translation (cm), rotation (quaternion x y z w) and scale, in the building's own space.</summary>
public sealed record ModelPart(string Mesh, double[] T, double[] R, double[] S);

/// <summary>Something that stands in the world of every save, like the rock of a resource node: a class of the library at a place.</summary>
public sealed record WorldObject(string Class, double[] T, double[] R, double[] S);

/// <summary>Index of the model library (models.json next to models.bin).</summary>
public sealed class ModelLibrary
{
    public const string IndexFile = "models.json", DataFile = "models.bin";

    /// <summary>Raised when the layout of models.bin or the meaning of the index changes.</summary>
    public const int FormatVersion = 4;
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

    /// <summary>The rocks, crystals and vents of the world's resource nodes.</summary>
    public List<WorldObject> Resources { get; set; } = [];

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
    private const int WantedTriangles = 250, RepeatedTriangles = 90, DetailedTriangles = 5000, LargeTriangles = 6000, ItemTriangles = 30;

    /// <summary>Helper geometry that is not part of what a building looks like.</summary>
    private static readonly string[] Ignored = ["Fog", "Clearance", "Hologram", "Arrow", "Collision", "Decal", "Snap", "Preview", "Guide", "Glow", "Trigger", "Smoke"];

    private static readonly string[] MeshComponentTypes = ["StaticMeshComponent", "FGColoredInstanceMeshProxy", "InstancedStaticMeshComponent", "FGProductionIndicatorInstanceComponent"];

    public const int VertexBytes = 20;
    private const int TextureSize = 512;
    private const string FactoryMaterial = "MM_Factory_Array", FactoryArray = "TX2D_FactoryBase_BC";

    /// <summary>Materials that are overlays or stand-ins for distant views, not surfaces of the model.</summary>
    /// <summary>Materials whose texture is black or dark where the game puts the paint.</summary>
    private static readonly string[] PaintedMaterials = ["MMU_Pipeline", "MMU_WaterPump"];
    private static readonly string[] StandIns = ["_IMP", "Imposter", "FactoryLod"];
    /// <summary>
    /// Not surfaces of the building: decals, stand-ins, and the planes the game draws smoke, fog and other
    /// effects on, which would show as solid slabs here.
    /// </summary>
    private static readonly string[] SkippedMaterials = ["Decal", "_IMP", "Imposter", "FactoryLod", "Hologram", "Smoke", "Effect", "InputFog"];

    private static readonly string[] AlbedoNames = ["Albedo", "BaseColor", "Base Color", "BC", "Diffuse", "Color", "Albedo_A", "MainTexture", "Texture"];
    private static readonly string[] MaskNames = ["AOMasks", "AO Masks", "AOMask", "AO_Masks"];
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
        var detailed = new HashSet<string>(StringComparer.Ordinal); // few of them and looked at closely: the pioneer
        var slotOverrides = new Dictionary<string, FPackageIndex?[]>(StringComparer.Ordinal); // meshes placed with other materials than their own
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var partKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var liftPieces = new Dictionary<string, string>(StringComparer.Ordinal); // what later lifts take over from Mk.1
        var items = new HashSet<string>(StringComparer.Ordinal); // tens of thousands on the belts, each a few centimetres

        foreach (string file in archive.Files.Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                                                         && Path.GetFileName(f).StartsWith("Build_", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var exports = archive.LoadExports(file).ToList();
                var parts = new List<ModelPart>();
                // A component can hang on another one (a group that is turned or moved as a whole); the
                // blueprint's construction script says which. Its place is then relative to that one.
                var hangsOn = new Dictionary<string, UObject>(StringComparer.Ordinal);
                foreach (var node in exports.Where(e => e.ExportType == "SCS_Node"))
                {
                    if (node.GetOrDefault<UObject?>("ComponentTemplate") is not { } template)
                        continue;
                    foreach (var child in node.GetOrDefault<UObject[]>("ChildNodes") ?? [])
                        if (child.GetOrDefault<UObject?>("ComponentTemplate") is { } hanging)
                            hangsOn[hanging.Name] = template;
                }
                (FVector T, FQuat R, FVector S) Placed(UObject component)
                {
                    var t = component.GetOrDefault("RelativeLocation", new FVector(0, 0, 0));
                    var r = component.GetOrDefault("RelativeRotation", new FRotator(0, 0, 0)).Quaternion();
                    var s = component.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1));
                    for (int depth = 0; depth < 16 && hangsOn.TryGetValue(component.Name, out var parent); depth++, component = parent)
                    {
                        var pt = parent.GetOrDefault("RelativeLocation", new FVector(0, 0, 0));
                        var pr = parent.GetOrDefault("RelativeRotation", new FRotator(0, 0, 0)).Quaternion();
                        var ps = parent.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1));
                        t = pt + pr.RotateVector(new FVector(t.X * ps.X, t.Y * ps.Y, t.Z * ps.Z));
                        r = pr * r;
                        s = new FVector(s.X * ps.X, s.Y * ps.Y, s.Z * ps.Z);
                    }
                    return (t, r, s);
                }
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
                        var (location, rotation, scale) = Placed(component);
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
                    var (location, rotation, scale) = Placed(component);
                    parts.Add(Part(mesh, location, rotation, scale));
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

        ReadResources(archive, library, meshObjects, slotOverrides);
        ReadPlayer(archive, library, meshObjects, detailed);
        ReadItems(archive, library, meshObjects, items);
        ReadWorldActors(archive, library, meshObjects, slotOverrides);
        // There are thousands of some of them all over the map: as coarse as the pieces of belts. The others
        // (crash sites, shrines, vehicles) are few, and looked at from close by; creatures as large as they are.
        foreach (var (name, parts) in library.Classes)
        {
            bool coarse = CoarseActors.Any(c => name.StartsWith(c, StringComparison.Ordinal));
            if (coarse || WorldActors.Any(a => name == a + "_C"))
                foreach (var part in parts)
                    (coarse ? repeated : detailed).Add(part.Mesh);
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
                    if (Write(mesh, slotOverrides.GetValueOrDefault(name) ?? Slots(mesh), stream, materials, items.Contains(name) ? ItemTriangles : repeated.Contains(name) ? RepeatedTriangles : detailed.Contains(name) ? DetailedTriangles : WantedTriangles) is { } written)
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
        // The second pipeline pump names no paint mask of its own; its texture is laid out like the first one's.
        if (library.Materials.FirstOrDefault(m => m is { Root: "MMU_Pump_01", Mask: not null }) is { } pump)
            for (int i = 0; i < library.Materials.Count; i++)
                if (library.Materials[i] is { Root: "MMU_Pump_01", Kind: "texture", Mask: null } other)
                    library.Materials[i] = other with { Mask = pump.Mask };
        library.Resources.RemoveAll(r => !library.Classes.ContainsKey(r.Class));
        library.Save(directory);
        log($"  3D models: {library.Classes.Count:N0} buildings and {library.Resources.Count:N0} resource nodes from {library.Meshes.Count:N0} meshes, " +
            $"{library.Meshes.Values.Sum(m => (long)m.I) / 3:N0} triangles, {new FileInfo(Path.Combine(directory, ModelLibrary.DataFile)).Length / 1e6:0.0} MB; " +
            $"{library.Materials.Count:N0} materials ({library.Materials.Count(m => m.Kind == "factory")} factory, {library.Materials.Count(m => m.Kind.StartsWith("texture", StringComparison.Ordinal))} textured), " +
            $"{Directory.GetFiles(textureDirectory).Length} textures, {Directory.GetFiles(textureDirectory).Sum(f => new FileInfo(f).Length) / 1e6:0.0} MB");
        return library;
    }

    /// <summary>Class of the model library that stands for a player.</summary>
    public const string PlayerClass = "Char_Player_C";
    private const string PlayerBlueprint = "FactoryGame/Content/FactoryGame/Character/Player/Char_Player.uasset";

    /// <summary>
    /// The pioneer: the body as it is modelled (arms slightly spread), and the helmet, which the game puts
    /// on the head bone.
    /// </summary>
    private static void ReadPlayer(GameArchive archive, ModelLibrary library, Dictionary<string, UObject> meshObjects, HashSet<string> detailed)
    {
        try
        {
            var exports = archive.LoadExports(PlayerBlueprint).ToList();
            if (exports.FirstOrDefault(e => e.Name == "CharacterMesh3P") is not { } body
                || !(body.TryGetValue(out FPackageIndex bodyIndex, "SkeletalMesh") || body.TryGetValue(out bodyIndex, "SkinnedAsset"))
                || Mesh(bodyIndex, meshObjects) is not { } bodyMesh || meshObjects[bodyMesh] is not USkeletalMesh skeleton)
                return;
            var location = body.GetOrDefault("RelativeLocation", new FVector(0, 0, 0));
            var rotation = body.GetOrDefault("RelativeRotation", new FRotator(0, 0, 0)).Quaternion();
            var parts = new List<ModelPart> { Part(bodyMesh, location, rotation, new FVector(1, 1, 1)) };
            detailed.Add(bodyMesh);

            var bones = skeleton.ReferenceSkeleton;
            int head = Array.FindIndex(bones.FinalRefBoneInfo, b => b.Name.Text.Equals("head", StringComparison.OrdinalIgnoreCase));
            if (head >= 0 && exports.FirstOrDefault(e => e.Name.StartsWith("HelmetMesh", StringComparison.Ordinal)) is { } helmet
                && helmet.TryGetValue(out FPackageIndex helmetIndex, "StaticMesh") && Mesh(helmetIndex, meshObjects) is { } helmetMesh)
            {
                // where the head is: every bone is placed relative to the one it hangs on
                var at = new FVector(0, 0, 0);
                var turned = FQuat.Identity;
                var chain = new List<int>();
                for (int bone = head; bone >= 0; bone = bones.FinalRefBoneInfo[bone].ParentIndex)
                    chain.Add(bone);
                for (int i = chain.Count - 1; i >= 0; i--)
                {
                    var pose = bones.FinalRefBonePose[chain[i]];
                    at += turned.RotateVector(pose.Translation);
                    turned *= pose.Rotation;
                }
                var own = helmet.GetOrDefault("RelativeRotation", new FRotator(0, 0, 0)).Quaternion();
                parts.Add(Part(helmetMesh, location + rotation.RotateVector(at), rotation * turned * own, new FVector(1, 1, 1)));
                detailed.Add(helmetMesh);
            }
            library.Classes[PlayerClass] = parts;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Without the model, players are only labelled.
        }
    }

    /// <summary>Class of the model library for an item on a belt: "Item:" and the item's descriptor class.</summary>
    public static string ItemClass(string descriptor) => "Item:" + descriptor;

    /// <summary>Class of the model library for a hand-mineable pile of a resource: "Deposit:" and the resource's descriptor class.</summary>
    public static string DepositClass(string descriptor) => "Deposit:" + descriptor;

    /// <summary>
    /// What items look like on a belt and on the ground: every item names the mesh for that, and resources
    /// another one for their piles to mine by hand.
    /// </summary>
    private static void ReadItems(GameArchive archive, ModelLibrary library, Dictionary<string, UObject> meshObjects, HashSet<string> items)
    {
        foreach (string file in archive.Files.Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                                                         && Path.GetFileName(f).StartsWith("Desc_", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                if (archive.LoadExports(file).FirstOrDefault(e => e.Name == "Default__" + name + "_C") is not { } defaults)
                    continue;
                if (defaults.TryGetValue(out FPackageIndex conveyor, "mConveyorMesh") && Mesh(conveyor, meshObjects) is { } item)
                {
                    library.Classes[ItemClass(name + "_C")] = [Part(item, new FVector(0, 0, 0), FQuat.Identity, new FVector(1, 1, 1))];
                    items.Add(item);
                }
                if (defaults.TryGetValue(out FPackageIndex deposit, "mDepositMesh") && Mesh(deposit, meshObjects) is { } pile)
                    library.Classes[DepositClass(name + "_C")] = [Part(pile, new FVector(0, 0, 0), FQuat.Identity, new FVector(1, 1, 1))];
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An item that cannot be read is left off the belts.
            }
        }
    }

    /// <summary>The things in the world that are not built: collectibles, plants, hazards, creatures, vehicles.</summary>
    private static readonly string[] WorldActors =
    [
        "BP_Crystal", "BP_Crystal_mk2", "BP_Crystal_mk3", "BP_WAT1", "BP_WAT2", "BP_MercerShrine", "BP_SomerSloopShrine", "BP_DropPod",
        "BP_BerryBush", "BP_NutBush", "BP_Shroom_01", "BP_SporeFlower", "BP_GasPillar_01", "BP_GasPillar_02", "BP_GasPillar_03", "BP_GasPillar_04",
        "BP_GasPillar_05", "BP_DestructibleLargeRock", "BP_DestructibleSmallRock", "BP_DestructibleFlatRock", "BP_Crate", "BP_WreckCrate",
        "BP_Truck", "BP_Tractor", "BP_Explorer", "BP_Locomotive", "BP_FreightWagon", "BP_DroneTransport", "BP_Golfcart", "BP_GolfcartGold",
    ];

    /// <summary>World objects there are thousands of, small and seen from afar; the others (crash sites, shrines) are few and large.</summary>
    private static readonly string[] CoarseActors =
        ["BP_Crystal", "BP_BerryBush", "BP_NutBush", "BP_Shroom", "BP_SporeFlower", "BP_GasPillar", "BP_Destructible", "Deposit:"];

    /// <summary>Meshes of these world objects that the game places or shows itself only at times (the berries on a bush).</summary>
    private static readonly string[] HiddenActorMeshes = ["SM_Berry_01"];

    /// <summary>
    /// The world objects of <see cref="WorldActors"/> and every creature, as their blueprints put them together:
    /// mesh components, also those the blueprint takes over from the one it is made from.
    /// </summary>
    private static void ReadWorldActors(GameArchive archive, ModelLibrary library, Dictionary<string, UObject> meshObjects, Dictionary<string, FPackageIndex?[]> slotOverrides)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in archive.Files.Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
            files.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        var names = WorldActors.Concat(files.Where(f => f.Value.Contains("/Character/Creature/", StringComparison.OrdinalIgnoreCase)
                                                        && f.Key.StartsWith("Char_", StringComparison.Ordinal)).Select(f => f.Key));
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var parts = new List<ModelPart>();
                var seen = new HashSet<string>(StringComparer.Ordinal); // components a blueprint made from another one changed
                for (string? current = name; current is not null && files.TryGetValue(current, out string? file) && seen.Count < 64;)
                {
                    var exports = archive.LoadExports(file).ToList();
                    var hangsOn = new Dictionary<string, UObject>(StringComparer.Ordinal);
                    foreach (var node in exports.Where(e => e.ExportType == "SCS_Node"))
                        if (node.GetOrDefault<UObject?>("ComponentTemplate") is { } template)
                            foreach (var child in node.GetOrDefault<UObject[]>("ChildNodes") ?? [])
                                if (child.GetOrDefault<UObject?>("ComponentTemplate") is { } hanging)
                                    hangsOn[hanging.Name] = template;
                    foreach (var component in exports.Where(e => e.ExportType is "StaticMeshComponent" or "SkeletalMeshComponent" or "FGColoredInstanceMeshProxy"))
                    {
                        string key = component.Name.Replace("_GEN_VARIABLE", "");
                        if (!seen.Add(key)
                            || Ignored.Any(i => component.Name.Contains(i, StringComparison.OrdinalIgnoreCase))
                            || (component.TryGetValue(out bool visible, "bVisible") && !visible)
                            || (component.TryGetValue(out bool hidden, "bHiddenInGame") && hidden)
                            || !(component.TryGetValue(out FPackageIndex meshIndex, "StaticMesh") || component.TryGetValue(out meshIndex, "SkeletalMesh") || component.TryGetValue(out meshIndex, "SkinnedAsset"))
                            || Mesh(meshIndex, meshObjects) is not { } mesh || HiddenActorMeshes.Contains(mesh))
                            continue;
                        var t = component.GetOrDefault("RelativeLocation", new FVector(0, 0, 0));
                        var r = component.GetOrDefault("RelativeRotation", new FRotator(0, 0, 0)).Quaternion();
                        var sc = component.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1));
                        for (var at = component; hangsOn.TryGetValue(at.Name, out var parent) && parts.Count < 256; at = parent)
                        {
                            var pt = parent.GetOrDefault("RelativeLocation", new FVector(0, 0, 0));
                            var pr = parent.GetOrDefault("RelativeRotation", new FRotator(0, 0, 0)).Quaternion();
                            var ps = parent.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1));
                            t = pt + pr.RotateVector(new FVector(t.X * ps.X, t.Y * ps.Y, t.Z * ps.Z));
                            r = pr * r;
                            sc = new FVector(sc.X * ps.X, sc.Y * ps.Y, sc.Z * ps.Z);
                        }
                        string variant = Variant(mesh, component.GetOrDefault<FPackageIndex?[]>("OverrideMaterials") ?? [], meshObjects, slotOverrides);
                        // the same mesh twice in the same place (a mesh and its stand-in for far views)
                        if (parts.Any(p => p.Mesh == variant && Math.Abs(p.T[0] - t.X) + Math.Abs(p.T[1] - t.Y) + Math.Abs(p.T[2] - t.Z) < 1))
                            continue;
                        parts.Add(Part(variant, t, r, sc));
                    }
                    current = exports.OfType<UClass>().FirstOrDefault()?.SuperStruct?.Name is { } super && super.EndsWith("_C", StringComparison.Ordinal)
                        ? super[..^2] : null;
                }
                if (parts.Count > 0)
                    library.Classes[name + "_C"] = parts;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Without its model the object is only a marker.
            }
        }
    }

    /// <summary>A mesh with some of its materials replaced; the mesh itself when none are.</summary>
    private static string Variant(string mesh, FPackageIndex?[] materials, Dictionary<string, UObject> meshObjects, Dictionary<string, FPackageIndex?[]> slotOverrides)
    {
        if (!materials.Any(m => m is { IsNull: false }))
            return mesh;
        string variant = mesh + "@" + string.Join("+", materials.Select(m => m is { IsNull: false } ? m.Name : ""));
        if (!meshObjects.ContainsKey(variant))
        {
            var slots = (FPackageIndex?[])Slots(meshObjects[mesh]).Clone();
            for (int i = 0; i < slots.Length && i < materials.Length; i++)
                if (materials[i] is { IsNull: false })
                    slots[i] = materials[i];
            slotOverrides[variant] = slots;
            meshObjects[variant] = meshObjects[mesh];
        }
        return variant;
    }

    private const string LevelDirectory = "/Map/GameLevel01/", ResourceMeshes = "/Game/FactoryGame/Resource/RawResources/";

    /// <summary>
    /// The look of the resource nodes: the nodes themselves are invisible, next to each stands an ordinary
    /// mesh of the level (a rock with ore, crystals, a vent). Some are the same mesh with the materials of
    /// another resource.
    /// </summary>
    private static void ReadResources(GameArchive archive, ModelLibrary library, Dictionary<string, UObject> meshObjects, Dictionary<string, FPackageIndex?[]> slotOverrides)
    {
        var found = new System.Collections.Concurrent.ConcurrentBag<(FPackageIndex Mesh, FPackageIndex?[] Materials, FVector T, FQuat R, FVector S)>();
        var levels = archive.Files.Where(f => f.Contains(LevelDirectory, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)).ToList();
        Parallel.ForEach(levels, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, level =>
        {
            try
            {
                foreach (var component in archive.LoadExports(level).Where(e => e.ExportType == "StaticMeshComponent"))
                {
                    if (!component.TryGetValue(out FPackageIndex mesh, "StaticMesh") || mesh.ResolvedObject?.GetPathName() is not { } path
                        || !path.StartsWith(ResourceMeshes, StringComparison.OrdinalIgnoreCase) || path.Contains("/Deposits/", StringComparison.OrdinalIgnoreCase)
                        || (component.TryGetValue(out bool visible, "bVisible") && !visible))
                        continue;
                    var t = component.GetOrDefault("RelativeLocation", new FVector(0, 0, 0));
                    var r = component.GetOrDefault("RelativeRotation", new FRotator(0, 0, 0)).Quaternion();
                    var s = component.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1));
                    // a mesh that hangs on another component of its actor is placed relative to that one
                    if (component.GetOrDefault<UObject?>("AttachParent") is { } parent)
                    {
                        var pt = parent.GetOrDefault("RelativeLocation", new FVector(0, 0, 0));
                        var pr = parent.GetOrDefault("RelativeRotation", new FRotator(0, 0, 0)).Quaternion();
                        var ps = parent.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1));
                        t = pt + pr.RotateVector(new FVector(t.X * ps.X, t.Y * ps.Y, t.Z * ps.Z));
                        r = pr * r;
                        s = new FVector(s.X * ps.X, s.Y * ps.Y, s.Z * ps.Z);
                    }
                    found.Add((mesh, component.GetOrDefault<FPackageIndex?[]>("OverrideMaterials") ?? [], t, r, s));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A level cell that cannot be read leaves out its nodes.
            }
        });

        foreach (var (meshIndex, materials, t, r, s) in found.OrderBy(f => f.T.X).ThenBy(f => f.T.Y))
        {
            if (Mesh(meshIndex, meshObjects) is not { } mesh)
                continue;
            // the mesh's own materials, except where the level names another one
            string variant = Variant(mesh, materials, meshObjects, slotOverrides);
            string name = "Resource:" + variant;
            library.Classes.TryAdd(name, [Part(variant, new FVector(0, 0, 0), FQuat.Identity, new FVector(1, 1, 1))]);
            library.Resources.Add(new WorldObject(name, [Round(t.X), Round(t.Y), Round(t.Z)], [Math.Round(r.X, 5), Math.Round(r.Y, 5), Math.Round(r.Z, 5), Math.Round(r.W, 5)],
                [Math.Round(s.X, 4), Math.Round(s.Y, 4), Math.Round(s.Z, 4)]));
        }
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

    private static ModelMesh? Write(UObject mesh, FPackageIndex?[] slots, Stream stream, MaterialTable materials, int wantedTriangles)
    {
        var lods = Levels(mesh);
        if (lods.Count == 0)
            return null;

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
        // Coarse levels can drop whole surfaces (the Crafting Bench keeps only its frame): for buildings only
        // levels that still have every material of the finest one. Small things in their thousands may lose some.
        var all = real.OrderByDescending(c => c.Lod.IndexCount).First().Materials.Where(m => m >= 0).ToHashSet();
        if (wantedTriangles >= WantedTriangles && real.Where(c => all.IsSubsetOf(c.Materials)).ToList() is { Count: > 0 } complete)
            real = complete;
        // A large building is looked at from close by and has many parts; the coarse levels of a building
        // 30 m high leave it a heap of blocks. The number wanted grows with the area it covers.
        if (wantedTriangles == WantedTriangles)
        {
            var finest = real.OrderByDescending(c => c.Lod.IndexCount).First().Lod;
            double extent = 0;
            for (int i = 0; i < finest.VertexCount; i += Math.Max(1, finest.VertexCount / 2000))
            {
                var v = finest.Position(i);
                extent = Math.Max(extent, Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))));
            }
            wantedTriangles = (int)Math.Clamp(wantedTriangles * Math.Pow(extent / 250, 2), wantedTriangles, LargeTriangles);
        }
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
            // Light added on top or a shimmer seen through (the rings of a Mercer Sphere), and surfaces the game
            // has dissolved completely, are no surfaces here. Glass is see-through too, but lit.
            if (chain.Last() is UMaterial { BlendMode: EBlendMode.BLEND_Additive or EBlendMode.BLEND_Modulate } or UMaterial { BlendMode: EBlendMode.BLEND_Translucent, ShadingModel: EMaterialShadingModel.MSM_Unlit }
                || chain.OfType<UMaterialInstanceConstant>().SelectMany(i => i.ScalarParameterValues ?? []).FirstOrDefault(v => v.ParameterInfo.Name.Text == "Dissolve") is { ParameterValue: >= 1 })
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
                // resource nodes name the texture that is really used "VT-Albedo"; their "Albedo" can be another resource's
                var albedo = parameters.FirstOrDefault(p => p.ParameterInfo.Name.Text.Equals("VT-Albedo", StringComparison.OrdinalIgnoreCase))
                             ?? parameters.FirstOrDefault(p => AlbedoNames.Contains(p.ParameterInfo.Name.Text, StringComparer.OrdinalIgnoreCase))
                             ?? parameters.FirstOrDefault(p => p.ParameterValue.ResolvedObject is { } t && AlbedoSuffixes.Any(s => t.Name.Text.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
                if (albedo?.ParameterValue.ResolvedObject is { } texture && Texture(texture.Name.Text, albedo.ParameterValue) is { } file)
                {
                    // Foundations hold a quarter of their top in the texture and lay it out mirrored around the middle.
                    bool mirrored = chain.Last().Name.StartsWith("MM_BakedStencil", StringComparison.Ordinal);
                    // Painted surfaces: the texture is dark where the paint goes and shows the bare metal elsewhere.
                    bool painted = PaintedMaterials.Contains(chain.Last().Name);
                    string? mask = PaintMask(chain);
                    var (glow, glowColor) = Glow(chain);
                    return new ModelMaterial(name, mirrored ? "texture-mirrored" : painted ? "texture-painted" : "texture", file, null, chain.Last().Name, painted ? null : mask)
                        { Glow = glow, GlowColor = glowColor };
                }
            }
            if (chain.Last() is UMaterial root)
            {
                var texture = root.ReferencedTextures?.FirstOrDefault(t => t is UTexture2D && AlbedoSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
                if (texture is not null && Texture(texture.Name, texture) is { } file)
                    return new ModelMaterial(name, PaintedMaterials.Contains(root.Name) ? "texture-painted" : "texture", file, null, chain.Last().Name,
                        PaintedMaterials.Contains(root.Name) ? null : PaintMask(chain));
            }

            // the ore in the middle of a resource node is bare metal or crystal without a colour texture
            string color = name.Contains("Middle_Iron", StringComparison.OrdinalIgnoreCase) ? "#8a6659"
                : name.Contains("Middle_Copper", StringComparison.OrdinalIgnoreCase) ? "#c0734a"
                : name.Contains("Middle_Gold", StringComparison.OrdinalIgnoreCase) ? "#d6b24c"
                : name.Contains("FrackingHole", StringComparison.OrdinalIgnoreCase) ? "#26221f"
                : name.Contains("Glass", StringComparison.OrdinalIgnoreCase) ? "#9fc4d8"
                : name.Contains("Concrete", StringComparison.OrdinalIgnoreCase) ? "#a9a9a4"
                : name.Contains("Rubber", StringComparison.OrdinalIgnoreCase) ? "#2b2b2d"
                : name.Contains("MercerSphere", StringComparison.OrdinalIgnoreCase) ? "#7a3e9d"
                : "#9aa0a6";
            return new ModelMaterial(name, "flat", null, color, chain.Last().Name);
        }

        /// <summary>
        /// Where a surface glows and in which colour: the emissive channel (blue) of its MREA map (metal,
        /// roughness, emissive, ambient occlusion), in the material's emissive colour. Null where it does not glow.
        /// </summary>
        private (string? File, string? Color) Glow(List<UObject> chain)
        {
            var instances = chain.OfType<UMaterialInstanceConstant>().ToList();
            var color = instances.SelectMany(i => i.VectorParameterValues ?? []).FirstOrDefault(v => v.ParameterInfo.Name.Text == "EmissiveColor");
            var map = instances.SelectMany(i => i.TextureParameterValues ?? []).FirstOrDefault(v => v.ParameterInfo.Name.Text == "ReflectionMap"
                                                                                                  && v.ParameterValue.ResolvedObject?.Name.Text.EndsWith("_MREA", StringComparison.OrdinalIgnoreCase) == true);
            if (color?.ParameterValue is not { } c || map?.ParameterValue.ResolvedObject is not { } texture)
                return (null, null);
            double strength = instances.SelectMany(i => i.ScalarParameterValues ?? []).FirstOrDefault(v => v.ParameterInfo.Name.Text == "EmissiveStrength")?.ParameterValue ?? 1;
            // far less than the game's bloom, which a plain colour cannot show: enough to light up the dark
            double scale = Math.Clamp(strength, 0, 4) * 0.35;
            static int Channel(double v) => (int)Math.Clamp(Math.Round(Math.Pow(Math.Clamp(v, 0, 1), 1 / 2.2) * 255), 0, 255);
            string name = texture.Name.Text + "_Glow";
            if (!_textures.TryGetValue(name, out string? file))
                _textures[name] = file = SaveChannel(name, map.ParameterValue.Load() as UTexture, 2);
            return file is null ? (null, null) : (file, $"#{Channel(c.R * scale):x2}{Channel(c.G * scale):x2}{Channel(c.B * scale):x2}");
        }

        /// <summary>One channel of a texture (0 red … 3 alpha) as a grey image.</summary>
        private string? SaveChannel(string name, UTexture? texture, int channel)
        {
            if (texture is null)
                return null;
            using var bitmap = GameArchive.Decode(texture, TextureSize);
            if (bitmap is null)
                return null;
            using var grey = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Gray8, SKAlphaType.Opaque);
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var p = bitmap.GetPixel(x, y);
                    byte v = channel switch { 0 => p.Red, 1 => p.Green, 2 => p.Blue, _ => p.Alpha };
                    grey.SetPixel(x, y, new SKColor(v, v, v));
                }
            string file = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_')) + ".jpg";
            using var stream = File.Create(Path.Combine(textureDirectory, file));
            grey.Encode(stream, SKEncodedImageFormat.Jpeg, 85);
            return file;
        }

        /// <summary>
        /// Most baked surfaces of the factory come with a mask of where they are painted: named by the
        /// material itself, or the one its master material uses when nothing else is said.
        /// </summary>
        private string? PaintMask(List<UObject> chain)
        {
            foreach (var owner in chain.OfType<UMaterialInstanceConstant>())
            {
                var masks = (owner.TextureParameterValues ?? []).FirstOrDefault(p => MaskNames.Contains(p.ParameterInfo.Name.Text, StringComparer.OrdinalIgnoreCase));
                if (masks?.ParameterValue.ResolvedObject is { } texture)
                    return Texture(texture.Name.Text, masks.ParameterValue);
            }
            var standard = (chain.Last() as UMaterial)?.ReferencedTextures?.FirstOrDefault(t => t is UTexture2D && t.Name.Contains("AOMask", StringComparison.OrdinalIgnoreCase));
            return standard is null ? null : Texture(standard.Name, standard);
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
