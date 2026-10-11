using System.Collections.Concurrent;
using System.Diagnostics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using SkiaSharp;

namespace SatisfactoryMapRenderer.Game;

/// <summary>
/// Map image, level actors and icons from the local game installation. Everything is extracted once per
/// game version into a cache directory; later runs only read the cache (icons for new items are extracted on demand).
/// </summary>
public sealed class GameAssets : IDisposable
{
    private const string LevelRoot = "FactoryGame/Content/FactoryGame/Map/GameLevel01/";
    private const string PersistentLevel = LevelRoot + "Persistent_Level.umap";
    private const string CellPrefix = LevelRoot + "Persistent_Level/_Generated_/";
    private const string MapSlices = "/Game/FactoryGame/Interface/UI/Assets/MapTest/SlicedMap/Map_{0}-{1}.Map_{0}-{1}";
    private const int IconSize = 256;

    /// <summary>Level actor classes worth putting on the map.</summary>
    public static readonly HashSet<string> ActorClasses = new(StringComparer.Ordinal)
    {
        "BP_ResourceNode_C", "BP_ResourceNodeGeyser_C", "BP_FrackingCore_C", "BP_FrackingSatellite_C", "BP_ResourceDeposit_C",
        "BP_Crystal_C", "BP_Crystal_mk2_C", "BP_Crystal_mk3_C", "BP_WAT1_C", "BP_WAT2_C", "BP_DropPod_C",
        "BP_BerryBush_C", "BP_NutBush_C", "BP_Shroom_01_C", "FGItemPickup_Spawnable", "BP_CreatureSpawner_C",
        "BP_SporeFlower_C", "BP_DestructibleLargeRock_C", "BP_DestructibleSmallRock_C", "BP_DestructibleFlatRock_C",
        "BP_GasPillar_01_C", "BP_GasPillar_02_C", "BP_GasPillar_03_C", "BP_GasPillar_04_C", "BP_GasPillar_05_C",
        "BP_MercerShrine_C", "BP_SomerSloopShrine_C",
    };

    /// <summary>Item descriptors whose icons are always extracted (collectibles and plants).</summary>
    private static readonly string[] FixedItemIcons =
    [
        "Desc_Crystal", "Desc_Crystal_mk2", "Desc_Crystal_mk3", "Desc_WAT1", "Desc_WAT2", "Desc_HardDrive",
        "Desc_Berry", "Desc_Nut", "Desc_Shroom",
    ];

    private readonly GameInstallation? _game;
    private readonly object _archiveLock = new();
    private readonly string _oodleCache;
    private readonly string? _oodleLibrary;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, string?> _iconFiles = new(StringComparer.Ordinal);
    private GameArchive? _archive;
    private Dictionary<string, string>? _descriptorFiles;

    private GameAssets(GameInstallation? game, string cacheDirectory, string oodleCache, string? oodleLibrary, Action<string> log)
    {
        _game = game;
        CacheDirectory = cacheDirectory;
        _oodleCache = oodleCache;
        _oodleLibrary = oodleLibrary;
        _log = log;
    }

    public GameData Data { get; private set; } = new();
    public string CacheDirectory { get; }
    public string IconDirectory => Path.Combine(CacheDirectory, "icons");

    /// <summary>The game's own icon, shown as the viewer's logo; null if the installation had none.</summary>
    public string? LogoPath => File.Exists(Path.Combine(CacheDirectory, LogoFileName)) ? Path.Combine(CacheDirectory, LogoFileName) : null;
    private const string LogoFileName = "logo.png";

    /// <summary>The in-game map with the terrain shaded from the game's height data; null if it could not be made.</summary>
    public string? ReliefImagePath => File.Exists(Path.Combine(CacheDirectory, ReliefFileName)) ? Path.Combine(CacheDirectory, ReliefFileName) : null;
    private const string ReliefFileName = "relief.jpg";
    /// <summary>The world's heights for the 3D view (16 bits per sample in the red and green channel); null without game data that has them.</summary>
    public string? TerrainHeightsPath => File.Exists(Path.Combine(CacheDirectory, HeightsFileName)) && File.Exists(Path.Combine(CacheDirectory, TerrainInfoFileName))
        ? Path.Combine(CacheDirectory, HeightsFileName) : null;
    public string TerrainInfoPath => Path.Combine(CacheDirectory, TerrainInfoFileName);
    private const string HeightsFileName = "heights.png";

    /// <summary>3D models of the buildables for the 3D view; null without game data that has them.</summary>
    public ModelLibrary? Models => _models ??= ModelLibrary.TryLoad(CacheDirectory);
    public string ModelDataPath => Path.Combine(CacheDirectory, ModelLibrary.DataFile);
    private ModelLibrary? _models;
    private const string TerrainInfoFileName = "terrain.json";
    private const string HeightData = "/Game/FactoryGame/Interface/UI/Assets/MapTest/HeightData_Test.HeightData_Test";
    public string? MapImagePath => Data.MapImage is { } name && File.Exists(Path.Combine(CacheDirectory, name)) ? Path.Combine(CacheDirectory, name) : null;

    /// <param name="cacheRoot">Root folder for all cached game data (per game version sub folders).</param>
    public static GameAssets Load(GameInstallation game, string cacheRoot, string? oodleLibrary, bool refresh, Action<string> log)
    {
        string versionKey = game.VersionKey;
        var assets = new GameAssets(game, Path.Combine(cacheRoot, $"game-{versionKey}"), Path.Combine(cacheRoot, "oodle"), oodleLibrary, log);
        string dataFile = assets.DataFile;
        var cached = refresh ? null : GameData.TryLoad(dataFile);
        if (cached is not null && (cached.MapImage is null || File.Exists(Path.Combine(assets.CacheDirectory, cached.MapImage))))
        {
            assets.Data = cached;
            log($"  Using cached game data ({cached.Actors.Count:N0} level actors) from {assets.CacheDirectory}");
            return assets;
        }

        log($"  Extracting game data from {game.Directory} (one-time, cached afterwards)...");
        // Start clean so every icon's name is captured again.
        if (Directory.Exists(assets.IconDirectory))
            Directory.Delete(assets.IconDirectory, recursive: true);
        Directory.CreateDirectory(assets.IconDirectory);
        assets.Data = assets.Extract(versionKey);
        if (game.TryReadIcon() is { } logo)
            File.WriteAllBytes(Path.Combine(assets.CacheDirectory, LogoFileName), logo);
        assets.CreateRelief();
        assets.CreateModels();
        assets.Data.Save(dataFile);
        return assets;
    }

    public const string DataFileName = "gamedata.json";

    /// <summary>Game data exported earlier with <see cref="ExportTo"/>; needs no game installation.</summary>
    public static GameAssets? LoadExported(string directory, Action<string> log)
    {
        if (GameData.TryLoad(Path.Combine(directory, DataFileName)) is not { } data)
            return null;
        var assets = new GameAssets(null, Path.GetFullPath(directory), "", null, log) { Data = data };
        log($"  {data.Actors.Count:N0} level actors, {(Directory.Exists(assets.IconDirectory) ? Directory.GetFiles(assets.IconDirectory, "*.png").Length : 0):N0} icons");
        return assets;
    }

    /// <summary>
    /// Writes everything a render can need into <paramref name="directory"/>: the level data and the icon of
    /// every item, building and creature in the game, so that any save can be rendered without the game.
    /// </summary>
    public void ExportTo(string directory)
    {
        foreach (var (name, file) in DescriptorFiles)
            Icon("item/" + name + "_C", () => IconTexture(DefaultObject(Archive, file)));

        string icons = Path.Combine(directory, "icons");
        Directory.CreateDirectory(icons);
        foreach (string png in Directory.GetFiles(IconDirectory, "*.png"))
            File.Copy(png, Path.Combine(icons, Path.GetFileName(png)), overwrite: true);
        if (MapImagePath is { } map)
            File.Copy(map, Path.Combine(directory, Path.GetFileName(map)), overwrite: true);
        if (_game?.TryReadIcon() is { } logo)
            File.WriteAllBytes(Path.Combine(directory, LogoFileName), logo);
        // Game data extracted by an older version of this tool has no relief map or terrain heights yet.
        if ((ReliefImagePath is null || TerrainHeightsPath is null) && _game is not null)
            CreateRelief();
        if (ReliefImagePath is { } relief)
            File.Copy(relief, Path.Combine(directory, ReliefFileName), overwrite: true);
        if (Models is null && _game is not null)
            CreateModels();
        if (Models is not null)
        {
            File.Copy(ModelDataPath, Path.Combine(directory, ModelLibrary.DataFile), overwrite: true);
            File.Copy(Path.Combine(CacheDirectory, ModelLibrary.IndexFile), Path.Combine(directory, ModelLibrary.IndexFile), overwrite: true);
        }
        if (TerrainHeightsPath is { } heights)
        {
            File.Copy(heights, Path.Combine(directory, HeightsFileName), overwrite: true);
            File.Copy(TerrainInfoPath, Path.Combine(directory, TerrainInfoFileName), overwrite: true);
        }
        File.Copy(DataFile, Path.Combine(directory, DataFileName), overwrite: true);
        _log($"  Exported {Directory.GetFiles(icons, "*.png").Length:N0} icons and {Data.Actors.Count:N0} level actors to {Path.GetFullPath(directory)}");
    }

    private string DataFile => Path.Combine(CacheDirectory, DataFileName);

    private GameArchive Archive
    {
        get
        {
            lock (_archiveLock)
                return _archive ??= GameArchive.Open(_game ?? throw new InvalidOperationException("No game installation; only exported game data is available."),
                    _oodleCache, _oodleLibrary, _log);
        }
    }

    // ------------------------------------------------------------------ extraction

    private GameData Extract(string versionKey)
    {
        var sw = Stopwatch.StartNew();
        var archive = Archive;
        var data = new GameData { GameVersionKey = versionKey, Engine = archive.Engine.ToString(), Names = ReadDisplayNames(_game!, _log) };

        data.MapImage = ExtractMap(archive);
        _log($"  Map image: {(data.MapImage is null ? "not found" : "8192 px")} ({sw.Elapsed.TotalSeconds:0.0}s)");

        data.Actors = ExtractActors(archive);
        _log($"  Level actors: {data.Actors.Count:N0} ({sw.Elapsed.TotalSeconds:0.0}s)");

        foreach (string descriptor in FixedItemIcons)
            ItemIcon(FindDescriptor(archive, descriptor) ?? descriptor);
        foreach (var resource in data.Actors.Select(a => a.Prop("resource")).OfType<string>().Distinct())
            ItemIcon(resource);
        data.CreatureIcons = ExtractCreatureIcons(archive);
        ExtractMapIcons(archive);
        _log($"  Icons: {Directory.GetFiles(IconDirectory, "*.png").Length} ({sw.Elapsed.TotalSeconds:0.0}s)");
        return data;
    }

    private void CreateModels()
    {
        try
        {
            _models = GameModels.Extract(Archive, CacheDirectory, _log);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The models are an extra; without them the 3D view shows boxes.
            _log($"  3D models: not read ({ex.Message})");
        }
    }

    /// <summary>
    /// Relief map: the in-game map with hill shading. The game ships a height map of the whole world (terrain,
    /// cliffs, rocks and large trees) that covers the same area as its map; light from the north-west turns it
    /// into shading, which is laid over the map's colours.
    /// </summary>
    private void CreateRelief()
    {
        try
        {
            if (MapImagePath is not { } mapPath || Archive.TryLoadFloatTexture(HeightData) is not var (heights, size))
                return;
            SaveTerrain(heights, size);

            // Shading per height sample: Sobel gradient (which also smooths the 16-bit steps), lit from the north-west.
            const float exaggeration = 800f;
            double azimuth = 315 * Math.PI / 180, altitude = 50 * Math.PI / 180;
            float flat = (float)Math.Sin(altitude);
            using var shade = new SKBitmap(size, size, SKColorType.Gray8, SKAlphaType.Opaque);
            byte[] gray = new byte[size * size];
            Parallel.For(0, size, y =>
            {
                int y0 = Math.Max(y - 1, 0), y1 = Math.Min(y + 1, size - 1);
                for (int x = 0; x < size; x++)
                {
                    int x0 = Math.Max(x - 1, 0), x1 = Math.Min(x + 1, size - 1);
                    float H(int px, int py) => heights[py * size + px];
                    float gx = (H(x1, y0) + 2 * H(x1, y) + H(x1, y1) - H(x0, y0) - 2 * H(x0, y) - H(x0, y1)) / 8 * exaggeration;
                    float gy = (H(x0, y1) + 2 * H(x, y1) + H(x1, y1) - H(x0, y0) - 2 * H(x, y0) - H(x1, y0)) / 8 * exaggeration;
                    double slope = Math.PI / 2 - Math.Atan(Math.Sqrt(gx * gx + gy * gy));
                    double aspect = Math.Atan2(-gx, gy);
                    double lit = Math.Clamp(Math.Sin(altitude) * Math.Sin(slope) + Math.Cos(altitude) * Math.Cos(slope) * Math.Cos(azimuth - aspect), 0, 1);
                    // Hard-light blending: 50 % grey leaves the map as it is, darker shades it, lighter brightens it.
                    double value = lit < flat ? 0.5 - (flat - lit) * 0.46 : 0.5 + (lit - flat) * 0.9;
                    gray[y * size + x] = (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
                }
            });
            gray.CopyTo(shade.GetPixelSpan());

            using var map = SKBitmap.Decode(mapPath);
            if (map is null)
                return;
            using var relief = new SKBitmap(map.Width, map.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(relief))
            using (var shadeImage = SKImage.FromBitmap(shade))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.HardLight })
            {
                canvas.DrawBitmap(map, 0, 0);
                canvas.DrawImage(shadeImage, new SKRect(0, 0, map.Width, map.Height), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            }
            using var stream = File.Create(Path.Combine(CacheDirectory, ReliefFileName));
            relief.Encode(stream, SKEncodedImageFormat.Jpeg, 90);
            _log($"  Relief map: {relief.Width} px from {size} px height data");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The relief map is an extra; the in-game map alone is enough.
            _log($"  Relief map: not created ({ex.Message})");
        }
    }

    /// <summary>
    /// Heights for the 3D view. The texture only holds values from 0 to 1; what they mean in the world is
    /// found by comparing them with actors that stand on the ground (resource nodes), whose height is known.
    /// </summary>
    private void SaveTerrain(float[] heights, int size)
    {
        using (var packed = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque))
        {
            var pixels = packed.GetPixelSpan();
            for (int i = 0; i < heights.Length; i++)
            {
                int value = (int)Math.Clamp(Math.Round(heights[i] * 65535.0), 0, 65535);
                pixels[i * 4] = (byte)(value >> 8);
                pixels[i * 4 + 1] = (byte)value;
                pixels[i * 4 + 3] = 255;
            }
            using var stream = File.Create(Path.Combine(CacheDirectory, HeightsFileName));
            packed.Encode(stream, SKEncodedImageFormat.Png, 100);
        }

        // Height value and world Z of every ground actor inside the map.
        var samples = new List<(double H, double Z)>();
        foreach (var actor in Data.Actors.Where(a => ResourceClasses.Contains(a.Class) || a.Class == "BP_ResourceDeposit_C"))
        {
            double px = (actor.X - World.WorldBounds.MinX) / World.WorldBounds.Size * (size - 1);
            double py = (actor.Y - World.WorldBounds.MinY) / World.WorldBounds.Size * (size - 1);
            if (px < 0 || py < 0 || px > size - 1 || py > size - 1)
                continue;
            int x0 = (int)px, y0 = (int)py, x1 = Math.Min(x0 + 1, size - 1), y1 = Math.Min(y0 + 1, size - 1);
            double fx = px - x0, fy = py - y0;
            double h = heights[y0 * size + x0] * (1 - fx) * (1 - fy) + heights[y0 * size + x1] * fx * (1 - fy)
                     + heights[y1 * size + x0] * (1 - fx) * fy + heights[y1 * size + x1] * fx * fy;
            samples.Add((h, actor.Z));
        }

        // Straight line Z = a·h + b, fitted three times while dropping what does not fit (nodes in caves lie
        // below the surface the height map shows).
        double a = 0, b = 0, deviation = 0;
        var used = samples;
        for (int pass = 0; pass < 4 && used.Count >= 20; pass++)
        {
            double meanH = used.Average(s => s.H), meanZ = used.Average(s => s.Z);
            double variance = used.Sum(s => (s.H - meanH) * (s.H - meanH));
            if (variance < 1e-9)
                break;
            a = used.Sum(s => (s.H - meanH) * (s.Z - meanZ)) / variance;
            b = meanZ - a * meanH;
            double slope = a, offset = b;
            deviation = Math.Sqrt(used.Average(s => Math.Pow(s.Z - (slope * s.H + offset), 2)));
            double limit = Math.Max(2 * deviation, 300);
            used = used.Where(s => Math.Abs(s.Z - (slope * s.H + offset)) <= limit).ToList();
        }
        if (a <= 0)
        {
            // No usable actors: assume the usual extent of the world, sea floor to the highest peaks.
            (a, b) = (60000, -8000);
        }

        File.WriteAllText(TerrainInfoPath, System.Text.Json.JsonSerializer.Serialize(new { size, zAt0 = Math.Round(b), zAt1 = Math.Round(a + b) }));
        _log($"  Terrain heights: {size} px, {b / 100:0} m to {(a + b) / 100:0} m (from {used.Count:N0} of {samples.Count:N0} ground actors, ±{deviation / 100:0.0} m)");
    }

    /// <summary>The in-game map is split into 2×2 textures; slice Map_A-B is column A, row B.</summary>
    private string? ExtractMap(GameArchive archive)
    {
        var slices = new SKBitmap?[2, 2];
        try
        {
            for (int a = 0; a < 2; a++)
                for (int b = 0; b < 2; b++)
                    slices[a, b] = archive.TryLoadTexture(string.Format(MapSlices, a, b), 0);
            if (slices.Cast<SKBitmap?>().Any(s => s is null))
                return null;

            int size = slices[0, 0]!.Width;
            using var map = new SKBitmap(size * 2, size * 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(map))
                for (int a = 0; a < 2; a++)
                    for (int b = 0; b < 2; b++)
                        canvas.DrawBitmap(slices[a, b], a * size, b * size);

            const string file = "map.jpg";
            using var stream = File.Create(Path.Combine(CacheDirectory, file));
            map.Encode(stream, SKEncodedImageFormat.Jpeg, 92);
            return file;
        }
        finally
        {
            foreach (var slice in slices)
                slice?.Dispose();
        }
    }

    private List<LevelActor> ExtractActors(GameArchive archive)
    {
        var packages = archive.Files
            .Where(f => f.StartsWith(CellPrefix, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .Prepend(PersistentLevel)
            .ToList();

        var actors = new ConcurrentBag<LevelActor>();
        int failed = 0;
        Parallel.ForEach(packages, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, package =>
        {
            try
            {
                foreach (var export in archive.LoadExports(package))
                    if (ActorClasses.Contains(export.ExportType) && ToLevelActor(export) is { } actor)
                        actors.Add(actor);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Interlocked.Increment(ref failed);
            }
        });
        if (failed > 0)
            _log($"  {failed} of {packages.Count} level packages could not be read.");

        var list = actors.OrderBy(a => a.Class, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
        ApplyClassDefaults(archive, list);
        return list;
    }

    private static LevelActor? ToLevelActor(UObject export)
    {
        if (!export.TryGetValue(out FPackageIndex root, "RootComponent") || root.IsNull || root.Load() is not { } component)
            return null;
        var location = component.GetOrDefault("RelativeLocation", new FVector(0, 0, 0));
        // Attached roots are rare for these actors; include the parent's offset when present.
        if (component.TryGetValue(out FPackageIndex parent, "AttachParent") && !parent.IsNull && parent.Load() is { } parentComponent)
            location += parentComponent.GetOrDefault("RelativeLocation", new FVector(0, 0, 0));

        var props = new Dictionary<string, string>();
        if (export.TryGetValue(out FPackageIndex resource, "mResourceClass") && resource.ResolvedObject is { } resourceClass)
            props["resource"] = resourceClass.GetPathName();
        if (export.TryGetValue(out FName purity, "mPurity"))
            props["purity"] = purity.Text.Split("::").Last();
        if (export.TryGetValue(out FPackageIndex creature, "mCreatureClass") && creature.ResolvedObject is { } creatureClass)
            props["creature"] = creatureClass.GetPathName();
        if (export.TryGetValue(out FStructFallback pickup, "mPickupItems") && pickup.TryGetValue(out int amount, "NumItems"))
            props["amount"] = amount.ToString();
        if (export.TryGetValue(out FSoftObjectPath shrine, "mShrineActorSomerSloop") || export.TryGetValue(out shrine, "mShrineActorMercer"))
            props["shrine"] = shrine.SubPathString.Split('.').Last();

        string label = export is AActor actor && !string.IsNullOrEmpty(actor.ActorLabel) ? actor.ActorLabel : export.Name;
        return new LevelActor(export.ExportType, export.Name, label, location.X, location.Y, location.Z, props.Count > 0 ? props : null);
    }

    private static readonly HashSet<string> ResourceClasses = new(StringComparer.Ordinal)
    {
        "BP_ResourceNode_C", "BP_ResourceNodeGeyser_C", "BP_FrackingCore_C", "BP_FrackingSatellite_C",
    };

    /// <summary>
    /// Level actors only store properties that differ from their class defaults (geysers never store their
    /// resource, many nodes not their purity), so missing values come from the class default object.
    /// </summary>
    private static void ApplyClassDefaults(GameArchive archive, List<LevelActor> actors)
    {
        var defaults = new Dictionary<string, (string? Resource, string Purity)>(StringComparer.Ordinal);
        for (int i = 0; i < actors.Count; i++)
        {
            var actor = actors[i];
            if (!ResourceClasses.Contains(actor.Class) || (actor.Prop("resource") is not null && actor.Prop("purity") is not null))
                continue;
            if (!defaults.TryGetValue(actor.Class, out var classDefaults))
            {
                classDefaults = (null, "RP_Normal");
                if (archive.TryLoadObject($"/Game/FactoryGame/Resource/{actor.Class[..^2]}.Default__{actor.Class}") is { } cdo)
                {
                    if (cdo.TryGetValue(out FPackageIndex resource, "mResourceClass") && resource.ResolvedObject is { } resourceClass)
                        classDefaults.Resource = resourceClass.GetPathName();
                    if (cdo.TryGetValue(out FName purity, "mPurity"))
                        classDefaults.Purity = purity.Text.Split("::").Last();
                }
                defaults[actor.Class] = classDefaults;
            }
            var props = new Dictionary<string, string>(actor.Props ?? []);
            if (!props.ContainsKey("resource") && classDefaults.Resource is not null)
                props["resource"] = classDefaults.Resource;
            props.TryAdd("purity", classDefaults.Purity);
            actors[i] = actor with { Props = props };
        }
    }

    private Dictionary<string, string> ExtractCreatureIcons(GameArchive archive)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in archive.Files.Where(f => f.Contains("/Creature/CreatureDescriptors/Desc_", StringComparison.OrdinalIgnoreCase)
                                                    && f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
        {
            var cdo = DefaultObject(archive, file);
            if (cdo is null || !cdo.TryGetValue(out FPackageIndex creature, "mCreatureClass") || creature.ResolvedObject is not { } creatureClass)
                continue;
            string key = "creature/" + ShortName(creatureClass.GetPathName());
            if (SaveIcon(key, IconTexture(cdo)))
                result[ShortName(creatureClass.GetPathName())] = key;
        }
        return result;
    }

    private void ExtractMapIcons(GameArchive archive)
    {
        foreach (var file in archive.Files.Where(f => f.Contains("/Interface/UI/Assets/Map/", StringComparison.OrdinalIgnoreCase)
                                                    && f.Contains("MapCompass_Icon_", StringComparison.OrdinalIgnoreCase)
                                                    && f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string key = "map/" + name[(name.IndexOf("Icon_", StringComparison.Ordinal) + 5)..].ToLowerInvariant();
            using var bitmap = archive.TryLoadTexture("/Game/" + file["FactoryGame/Content/".Length..^".uasset".Length] + "." + name, IconSize);
            SaveIcon(key, bitmap);
        }
    }

    // ------------------------------------------------------------------ icons

    /// <summary>Icon of an item descriptor ("/Game/.../Desc_OreIron.Desc_OreIron_C"); extracted on first use.</summary>
    public string? ItemIcon(string descriptorPath)
    {
        string key = "item/" + ShortName(descriptorPath);
        return Icon(key, () =>
        {
            var cdo = descriptorPath.StartsWith('/') ? DefaultObject(Archive, GameArchive.PackageFile(descriptorPath)) : null;
            // Saves from older game versions refer to descriptors that have moved since; find them by name.
            string name = ShortName(descriptorPath);
            if (cdo is null && DescriptorFiles.TryGetValue(name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name, out var file))
                cdo = DefaultObject(Archive, file);
            return IconTexture(cdo);
        });
    }

    /// <summary>Icon of a building class ("Build_ConstructorMk1_C"), taken from its descriptor Desc_ConstructorMk1.</summary>
    public string? BuildingIcon(string buildingClass)
    {
        if (!buildingClass.StartsWith("Build_", StringComparison.Ordinal) || !buildingClass.EndsWith("_C", StringComparison.Ordinal))
            return null;
        string descriptor = "Desc_" + buildingClass["Build_".Length..^2];
        return Icon("item/" + descriptor + "_C", () =>
            IconTexture(DescriptorFiles.TryGetValue(descriptor, out var file) ? DefaultObject(Archive, file) : null));
    }

    /// <summary>Every descriptor package in the game: "Desc_ConstructorMk1" → package file.</summary>
    private Dictionary<string, string> DescriptorFiles
    {
        get
        {
            lock (_iconFiles)
            {
                if (_descriptorFiles is null)
                {
                    _descriptorFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in Archive.Files)
                        if (file.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(file) is var name
                            && name.StartsWith("Desc_", StringComparison.OrdinalIgnoreCase))
                            _descriptorFiles.TryAdd(name, file);
                }
                return _descriptorFiles;
            }
        }
    }

    /// <summary>In-game English name of an item or building class (path or short class name).</summary>
    public string? DisplayName(string classPath) => Data.Names.GetValueOrDefault(ShortName(classPath));

    public string? CreatureIcon(string creatureClass) =>
        Data.CreatureIcons.TryGetValue(creatureClass, out var key) ? Icon(key, () => null) : null;

    /// <summary>Reads CommunityResources/Docs/en-US.json (UTF-16): class name → mDisplayName for items and buildings.</summary>
    private static Dictionary<string, string> ReadDisplayNames(GameInstallation game, Action<string> log)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(game.DocsFile))
            return names;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(game.DocsFile));
            foreach (var group in doc.RootElement.EnumerateArray())
                if (group.TryGetProperty("Classes", out var classes))
                    foreach (var cls in classes.EnumerateArray())
                        if (cls.TryGetProperty("ClassName", out var className) && cls.TryGetProperty("mDisplayName", out var displayName)
                            && displayName.GetString() is { Length: > 0 } name)
                            names.TryAdd(className.GetString()!, name);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            log($"  Could not read {game.DocsFile} ({ex.Message}); using generated names.");
        }
        return names;
    }

    public string? MapIcon(string name) => Icon("map/" + name, () => null);

    /// <summary>Returns the icon key if its PNG exists (extracting it with <paramref name="load"/> if needed).</summary>
    private string? Icon(string key, Func<SKBitmap?> load)
    {
        return _iconFiles.GetOrAdd(key, k =>
        {
            if (File.Exists(IconPath(k)))
                return k;
            // An empty marker file remembers that the game has no such icon, so later runs need not open the archives.
            string missing = IconPath(k) + ".none";
            if (File.Exists(missing))
                return null;
            try
            {
                using var bitmap = load();
                if (SaveIcon(k, bitmap))
                    return k;
                Directory.CreateDirectory(IconDirectory);
                File.WriteAllBytes(missing, []);
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        });
    }

    public string IconPath(string key) => Path.Combine(IconDirectory, key.Replace('/', '-') + ".png");

    private bool SaveIcon(string key, SKBitmap? bitmap)
    {
        if (bitmap is null)
            return false;
        Directory.CreateDirectory(IconDirectory);
        using var stream = File.Create(IconPath(key));
        bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        _iconFiles[key] = key;
        return true;
    }

    private static SKBitmap? IconTexture(UObject? descriptor)
    {
        if (descriptor is null)
            return null;
        foreach (var name in new[] { "mPersistentBigIcon", "mSmallIcon" })
            if (descriptor.TryGetValue(out FPackageIndex icon, name) && !icon.IsNull && icon.Load() is UTexture texture
                && GameArchive.Decode(texture, IconSize) is { } bitmap)
                return bitmap;
        return null;
    }

    private static UObject? DefaultObject(GameArchive archive, string packageFile)
    {
        try
        {
            return archive.LoadExports(packageFile).FirstOrDefault(e => e.Name.StartsWith("Default__", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string? FindDescriptor(GameArchive archive, string name)
    {
        string file = archive.Files.FirstOrDefault(f => f.EndsWith("/" + name + ".uasset", StringComparison.OrdinalIgnoreCase)
                                                       && f.Contains("/Resource/", StringComparison.OrdinalIgnoreCase)) ?? "";
        return file.Length == 0 ? null : "/Game/" + file["FactoryGame/Content/".Length..^".uasset".Length] + "." + name + "_C";
    }

    private static string ShortName(string path)
    {
        int dot = path.LastIndexOf('.');
        return dot >= 0 ? path[(dot + 1)..] : path;
    }

    public void Dispose() => _archive?.Dispose();
}
