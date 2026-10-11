using System.Collections.Concurrent;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using SkiaSharp;

namespace SatisfactoryMapRenderer.Game;

/// <summary>
/// The ground of the 3D view: which of the game's ground textures lies where.
/// </summary>
/// <param name="MinX">World position (cm) of the first sample of <see cref="GameGround.MapFile"/>.</param>
/// <param name="Cell">Distance between two samples (cm).</param>
/// <param name="Layers">The texture images, in the order of the layer numbers in the map.</param>
/// <param name="Tile">Edge length (cm) of one repetition of a texture.</param>
/// <param name="DepthStep">Water depth (cm) per step of the map's blue channel.</param>
public sealed record GroundInfo(int FormatVersion, double MinX, double MinY, double Cell, int Width, int Height, string[] Layers, double Tile, double DepthStep);

/// <summary>
/// Reads the landscape of the game's world: 2,289 tiles of 127 × 127 m, each with the weights of the ground
/// layers painted on it (sand, grass, cliff, …) and its heights, one sample per metre. Together with the
/// game's water surfaces this becomes one image (ground.png) that says for every 2 × 2 m which two layers
/// meet there and how deep the water is, plus one tiling texture per layer.
///
/// ground.png: red = first layer × 16 + second layer (15 = no landscape there), green = share of the first
/// layer (255 = all), blue = water depth in steps of <see cref="DepthStep"/> cm (0 = dry).
/// </summary>
public static class GameGround
{
    public const string Directory = "ground", InfoFile = "ground.json", MapFile = "ground.png";
    public const int FormatVersion = 1;
    private const string LevelDirectory = "/Map/GameLevel01/";
    private const string TextureRoot = "/Game/FactoryGame/World/Environment/Landscape/Texture/Tiles/";
    private const int TileQuads = 127, TileSamples = 128, Step = 2, None = 15;
    private const double DepthStep = 25, TextureTile = 600;
    private const int TextureSize = 512;

    /// <summary>
    /// The ground layers that have a look, with the game's texture for each. The master material that blends
    /// them is compiled, so the assignment follows the names. Wet sand has no texture of its own in the game
    /// either: it is the sand, darkened.
    /// </summary>
    private static readonly (string Layer, string File, string Texture, float Tint)[] Layers =
    [
        ("Sand", "sand", "Sand/TX_Sand_BC", 1f),
        ("SandRipples", "sand-ripples", "Sand/TX_SandRipples_BC", 1f),
        ("WetSand", "sand-wet", "Sand/TX_Sand_BC", 0.72f),
        ("SandPebbles", "sand-pebbles", "Pebbels/TX_SandPebbles_01_Alb", 1f),
        ("SandRock", "sand-rock", "SandRock/TX_SandRock_Alb_01", 1f),
        ("SandCracks", "sand-cracks", "Sand/Sand_Dry_02_Alb", 1f),
        ("Soil", "soil", "Soil/TX_Soil_01_Alb", 1f),
        ("Puddles", "puddles", "Soil/TX_Puddles_01_Alb", 1f),
        ("Grass", "grass", "Grass/TX_Grass_01_Alb", 1f),
        ("GrassRed", "grass-red", "GrassRed/TX_GrassRed_01_Alb", 1f),
        ("Forest", "forest", "Forest/TX_Forest_01_Alb", 1f),
        ("RedJungle", "red-jungle", "RedJungle/TX_Grass_RedJungle_01_Alb", 1f),
        ("Gravel", "gravel", "Stones/Gravel_Alb", 1f),
        ("Cliff", "cliff", "Cliff/Cliff_Detail_Alb", 1f),
        ("CoralRock", "coral-rock", "SeaRocks/TX_SeaRocks_01_Alb", 1f),
    ];
    /// <summary>Layers painted on a single tile each; they look like one of the others.</summary>
    private static readonly Dictionary<string, string> Aliases = new() { ["DesertRock"] = "SandRock", ["PurpleForest"] = "Forest" };

    /// <summary>One landscape tile: per sample the two strongest layers, the share of the first, and the height.</summary>
    private sealed record Tile(int X, int Y, byte[] First, byte[] Second, byte[] Share, ushort[] Height);

    /// <summary>A triangle of a water surface, in world coordinates (cm).</summary>
    private readonly record struct Triangle(FVector A, FVector B, FVector C, bool Sea = false);

    public static GroundInfo? TryLoad(string gameDataDirectory)
    {
        try
        {
            string file = Path.Combine(gameDataDirectory, Directory, InfoFile);
            if (!File.Exists(file) || !File.Exists(Path.Combine(gameDataDirectory, Directory, MapFile)))
                return null;
            var info = JsonSerializer.Deserialize<GroundInfo>(File.ReadAllText(file));
            return info is { FormatVersion: FormatVersion } ? info : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <param name="heights">
    /// The height image of the 3D view and its description, with the world's extent: the landscape's exact
    /// heights are used to say what the image's values mean in the world. Null to leave them as they are.
    /// </param>
    public static GroundInfo? Extract(GameArchive archive, string gameDataDirectory, Action<string> log,
        (string Image, string Info, double MinX, double MinY, double Size)? heights = null)
    {
        var tiles = new ConcurrentBag<Tile>();
        var water = new ConcurrentBag<Triangle>();
        var levels = archive.Files.Where(f => f.Contains(LevelDirectory, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)).ToList();
        FVector origin = default, scale = new(100, 100, 100);
        bool found = false;
        Parallel.ForEach(levels, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, level =>
        {
            try
            {
                foreach (var export in archive.LoadExports(level))
                {
                    switch (export.ExportType)
                    {
                        case "LandscapeComponent":
                            if (ReadTile(export) is { } tile)
                                tiles.Add(tile);
                            break;
                        case "LandscapeStreamingProxy" when export.GetOrDefault<UObject?>("RootComponent") is { } root:
                            // Every part of the landscape says where the whole begins: its position minus its offset in samples.
                            var offset = export.GetOrDefault<FIntPoint>("LandscapeSectionOffset");
                            var location = root.GetOrDefault<FVector>("RelativeLocation");
                            scale = root.GetOrDefault("RelativeScale3D", new FVector(100, 100, 100));
                            origin = new FVector(location.X - offset.X * scale.X, location.Y - offset.Y * scale.Y, location.Z);
                            found = true;
                            break;
                        case "BP_Water_C" or "BP_LakeWater_C" or "BP_TranslucentWater_C":
                            foreach (var t in WaterPlane(export))
                                water.Add(t);
                            break;
                        case "BPW_OceanSplineTool_02_C":
                            foreach (var t in Ocean(export))
                                water.Add(t);
                            break;
                        case "BP_River_PROT_C":
                            foreach (var t in River(export))
                                water.Add(t);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A level cell that cannot be read leaves a gap, where the in-game map shows through.
            }
        });
        if (tiles.IsEmpty || !found)
        {
            log("  Ground: the game's landscape was not found");
            return null;
        }

        int minX = tiles.Min(t => t.X), minY = tiles.Min(t => t.Y);
        int countX = (tiles.Max(t => t.X) - minX) / TileQuads + 1, countY = (tiles.Max(t => t.Y) - minY) / TileQuads + 1;
        var grid = new Tile?[countX * countY];
        foreach (var tile in tiles)
            grid[(tile.Y - minY) / TileQuads * countX + (tile.X - minX) / TileQuads] = tile;

        int width = countX * TileQuads / Step + 1, height = countY * TileQuads / Step + 1;
        double worldX = origin.X + minX * scale.X, worldY = origin.Y + minY * scale.Y, cell = Step * scale.X;
        float[] surface = WaterSurface(water, width, height, worldX, worldY, cell);

        // Red, green, blue per sample; see the class comment.
        byte[] map = new byte[width * height * 3];
        float[] ground = new float[width * height];
        long wet = 0, covered = 0;
        Parallel.For(0, height, v =>
        {
            Span<int> weight = stackalloc int[Layers.Length];
            for (int u = 0; u < width; u++)
            {
                weight.Clear();
                int samples = 0;
                double sum = 0;
                // The sample stands for the 2 × 2 m around it.
                for (int dy = 0; dy < Step; dy++)
                {
                    for (int dx = 0; dx < Step; dx++)
                    {
                        int x = Math.Min(u * Step + dx, countX * TileQuads), y = Math.Min(v * Step + dy, countY * TileQuads);
                        int tx = Math.Min(x / TileQuads, countX - 1), ty = Math.Min(y / TileQuads, countY - 1);
                        if (grid[ty * countX + tx] is not { } tile)
                            continue;
                        int i = (y - ty * TileQuads) * TileSamples + x - tx * TileQuads;
                        if (tile.First[i] == None)
                            continue;
                        weight[tile.First[i]] += tile.Share[i];
                        if (tile.Second[i] != None)
                            weight[tile.Second[i]] += 255 - tile.Share[i];
                        sum += (tile.Height[i] - 32768) / 128.0 * scale.Z + origin.Z;
                        samples++;
                    }
                }

                int o = (v * width + u) * 3;
                if (samples < Step * Step / 2)
                {
                    map[o] = None << 4 | None;
                    ground[v * width + u] = float.NaN;
                    continue;
                }
                ground[v * width + u] = (float)(sum / samples);
                int first = 0, second = -1;
                for (int l = 1; l < weight.Length; l++)
                    if (weight[l] > weight[first])
                        first = l;
                for (int l = 0; l < weight.Length; l++)
                    if (l != first && weight[l] > 0 && (second < 0 || weight[l] > weight[second]))
                        second = l;
                map[o] = (byte)(first << 4 | (second < 0 ? None : second));
                map[o + 1] = (byte)(second < 0 ? 255 : Math.Round(255.0 * weight[first] / (weight[first] + weight[second])));
                Interlocked.Increment(ref covered);
            }
        });
        FillSea(surface, ground, width, height, water.Where(t => t.Sea).Select(t => t.A.Z).DefaultIfEmpty(float.NaN).Order().ElementAt(water.Count(t => t.Sea) / 2));
        if (heights is { } h)
            Calibrate(ground, surface, width, height, worldX, worldY, cell, h, log);
        for (int i = 0; i < ground.Length; i++)
        {
            if (float.IsNaN(ground[i]) || surface[i] <= ground[i])
                continue;
            map[i * 3 + 2] = (byte)Math.Clamp(Math.Ceiling((surface[i] - ground[i]) / DepthStep), 1, 255);
            wet++;
        }

        string directory = Path.Combine(gameDataDirectory, Directory);
        if (System.IO.Directory.Exists(directory))
            System.IO.Directory.Delete(directory, recursive: true);
        System.IO.Directory.CreateDirectory(directory);
        using (var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque)))
        {
            var pixels = bitmap.GetPixelSpan();
            for (int i = 0; i < width * height; i++)
            {
                map.AsSpan(i * 3, 3).CopyTo(pixels.Slice(i * 4, 3));
                pixels[i * 4 + 3] = 255;
            }
            using var stream = File.Create(Path.Combine(directory, MapFile));
            bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        }

        var files = new List<string>();
        foreach (var (layer, file, texture, tint) in Layers)
        {
            string name = texture[(texture.LastIndexOf('/') + 1)..];
            using var image = archive.TryLoadTexture($"{TextureRoot}{texture}.{name}", TextureSize)
                              ?? throw new InvalidOperationException($"the texture of the ground layer {layer} was not found");
            if (tint < 1f)
            {
                var pixels = image.GetPixelSpan();
                for (int i = 0; i < pixels.Length; i++)
                    if (i % 4 != 3)
                        pixels[i] = (byte)(pixels[i] * tint);
            }
            using var stream = File.Create(Path.Combine(directory, file + ".jpg"));
            image.Encode(stream, SKEncodedImageFormat.Jpeg, 88);
            files.Add(file + ".jpg");
        }

        var info = new GroundInfo(FormatVersion, worldX, worldY, cell, width, height, [.. files], TextureTile, DepthStep);
        File.WriteAllText(Path.Combine(directory, InfoFile), JsonSerializer.Serialize(info));
        long bytes = new DirectoryInfo(directory).EnumerateFiles().Sum(f => f.Length);
        log($"  Ground: {tiles.Count:N0} landscape tiles, {Layers.Length} layers, {width} × {height} samples of {cell / 100:0} m, " +
            $"{100.0 * wet / Math.Max(covered, 1):0}% under water ({water.Count:N0} water triangles), {bytes / 1048576.0:0.0} MB");
        return info;
    }

    private static Tile? ReadTile(UObject component)
    {
        if (component.GetOrDefault<int>("ComponentSizeQuads") != TileQuads
            || component.GetOrDefault<UTexture2D?>("HeightmapTexture")?.GetFirstMip() is not { SizeX: TileSamples, SizeY: TileSamples, BulkData.Data: { } heights })
            return null;
        var textures = (component.GetOrDefault<UTexture2D[]>("WeightmapTextures") ?? [])
            .Select(t => t.GetFirstMip() is { SizeX: TileSamples, SizeY: TileSamples, BulkData.Data: { } data } && t.Format == EPixelFormat.PF_B8G8R8A8 ? data : null).ToArray();

        // Per painted layer: its number here (-1 = the layer that cuts holes, -2 = no look) and where its weights are.
        var allocations = new List<(int Layer, byte[] Data, int Offset)>();
        foreach (var allocation in component.GetOrDefault<FStructFallback[]>("WeightmapLayerAllocations") ?? [])
        {
            string name = allocation.GetOrDefault<FPackageIndex?>("LayerInfo")?.Name ?? "";
            name = name.Replace("_WeightLayerInfo", "").Replace("_LayerInfo", "");
            int texture = allocation.GetOrDefault<byte>("WeightmapTextureIndex"), channel = allocation.GetOrDefault<byte>("WeightmapTextureChannel");
            if (texture >= textures.Length || textures[texture] is not { } data || channel > 3)
                continue;
            int layer = name.StartsWith("LandscapeVisibility", StringComparison.Ordinal) ? -1
                : Array.FindIndex(Layers, l => l.Layer == Aliases.GetValueOrDefault(name, name)) is >= 0 and var known ? known : -2;
            // The pixels are blue, green, red, alpha; the channels count red, green, blue, alpha.
            allocations.Add((layer, data, channel == 3 ? 3 : 2 - channel));
        }

        int count = TileSamples * TileSamples;
        var tile = new Tile(component.GetOrDefault<int>("SectionBaseX"), component.GetOrDefault<int>("SectionBaseY"), new byte[count], new byte[count], new byte[count], new ushort[count]);
        for (int i = 0; i < count; i++)
        {
            tile.Height[i] = (ushort)(heights[i * 4 + 2] << 8 | heights[i * 4 + 1]);
            int first = None, second = None, firstWeight = 0, secondWeight = 0;
            bool hole = false;
            foreach (var (layer, data, offset) in allocations)
            {
                int weight = data[i * 4 + offset];
                if (layer == -1)
                    hole |= weight > 127;
                if (layer < 0 || weight == 0)
                    continue;
                if (weight > firstWeight)
                    (second, secondWeight, first, firstWeight) = (first, firstWeight, layer, weight);
                else if (weight > secondWeight)
                    (second, secondWeight) = (layer, weight);
            }
            if (hole)
                first = second = None;
            tile.First[i] = (byte)first;
            tile.Second[i] = (byte)second;
            tile.Share[i] = (byte)(second == None ? 255 : 255 * firstWeight / (firstWeight + secondWeight));
        }
        return tile;
    }

    /// <summary>
    /// The height image holds values from 0 to 1. The landscape's heights are exact, so comparing both where
    /// the ground is dry gives the straight line between value and world height. Rocks standing on the
    /// landscape are in the image but not in the landscape; such samples are dropped pass by pass.
    /// </summary>
    private static void Calibrate(float[] ground, float[] surface, int width, int height, double worldX, double worldY, double cell,
        (string Image, string Info, double MinX, double MinY, double Size) heights, Action<string> log)
    {
        using var image = SKBitmap.Decode(heights.Image);
        if (image is null || image.Width != image.Height)
            return;
        int size = image.Width;
        var samples = new List<(double H, double Z)>();
        for (int v = 0; v < height; v += 6)
        {
            for (int u = 0; u < width; u += 6)
            {
                int i = v * width + u;
                if (float.IsNaN(ground[i]) || surface[i] > ground[i] - 50)
                    continue;
                int px = (int)Math.Round((worldX + u * cell - heights.MinX) / heights.Size * (size - 1)), py = (int)Math.Round((worldY + v * cell - heights.MinY) / heights.Size * (size - 1));
                if (px < 0 || py < 0 || px >= size || py >= size)
                    continue;
                var c = image.GetPixel(px, py);
                samples.Add(((c.Red * 256 + c.Green) / 65535.0, ground[i]));
            }
        }

        double a = 0, b = 0, deviation = 0;
        var used = samples;
        for (int pass = 0; pass < 10 && used.Count >= 1000; pass++)
        {
            double meanH = used.Average(s => s.H), meanZ = used.Average(s => s.Z);
            double variance = used.Sum(s => (s.H - meanH) * (s.H - meanH));
            if (variance < 1e-9)
                return;
            a = used.Sum(s => (s.H - meanH) * (s.Z - meanZ)) / variance;
            b = meanZ - a * meanH;
            double slope = a, offset = b;
            deviation = Math.Sqrt(used.Average(s => Math.Pow(s.Z - (slope * s.H + offset), 2)));
            double limit = Math.Max(1.5 * deviation, 150);
            used = used.Where(s => Math.Abs(s.Z - (slope * s.H + offset)) <= limit).ToList();
        }
        if (a <= 0 || used.Count < 1000)
            return;
        File.WriteAllText(heights.Info, JsonSerializer.Serialize(new { size, zAt0 = Math.Round(b), zAt1 = Math.Round(a + b) }));
        log($"  Terrain heights: set to the landscape, {b / 100:0} m to {(a + b) / 100:0} m (from {used.Count:N0} of {samples.Count:N0} samples, ±{deviation / 100:0.0} m)");
    }

    // ---------------------------------------------------------------- water

    private static (FVector Location, double Cos, double Sin, FVector Scale) Placement(UObject root)
    {
        double yaw = root.GetOrDefault<FRotator>("RelativeRotation").Yaw * Math.PI / 180;
        return (root.GetOrDefault<FVector>("RelativeLocation"), Math.Cos(yaw), Math.Sin(yaw), root.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1)));
    }

    private static FVector ToWorld((FVector Location, double Cos, double Sin, FVector Scale) p, double x, double y, double z) =>
        new((float)(p.Location.X + x * p.Scale.X * p.Cos - y * p.Scale.Y * p.Sin), (float)(p.Location.Y + x * p.Scale.X * p.Sin + y * p.Scale.Y * p.Cos), (float)(p.Location.Z + z * p.Scale.Z));

    /// <summary>Lakes and ponds: a flat square of 1 × 1 m, scaled and turned.</summary>
    private static IEnumerable<Triangle> WaterPlane(UObject actor)
    {
        if (actor.GetOrDefault<UObject?>("RootComponent") is not { } root)
            yield break;
        var p = Placement(root);
        FVector a = ToWorld(p, -50, -50, 0), b = ToWorld(p, 50, -50, 0), c = ToWorld(p, 50, 50, 0), d = ToWorld(p, -50, 50, 0);
        yield return new Triangle(a, b, c);
        yield return new Triangle(a, c, d);
    }

    /// <summary>The sea: an outline drawn with a few points, filled.</summary>
    private static IEnumerable<Triangle> Ocean(UObject actor)
    {
        if (actor.GetOrDefault<UObject?>("RootComponent") is not { } root || actor.GetOrDefault<UObject?>("Spline") is not { } spline)
            yield break;
        var p = Placement(root);
        var points = SplinePoints(spline, "Position").Select(v => ToWorld(p, v.Out.X, v.Out.Y, v.Out.Z)).ToList();
        for (int i = 1; i + 1 < points.Count; i++)
            yield return new Triangle(points[0], points[i], points[i + 1], Sea: true);
    }

    /// <summary>A river: a band of 10 m, scaled in width, along a curve.</summary>
    private static IEnumerable<Triangle> River(UObject actor)
    {
        if (actor.GetOrDefault<UObject?>("mSplineComponent") is not { } spline)
            yield break;
        var p = Placement(spline);
        var positions = SplinePoints(spline, "Position");
        var scales = SplinePoints(spline, "Scale");
        const int Pieces = 6;
        FVector? lastLeft = null, lastRight = null;
        for (int i = 0; i + 1 < positions.Count; i++)
        {
            var (p0, _, m0) = positions[i];
            var (p1, m1, _) = positions[i + 1];
            double w0 = i < scales.Count ? scales[i].Out.Y : 1, w1 = i + 1 < scales.Count ? scales[i + 1].Out.Y : w0;
            for (int k = i == 0 ? 0 : 1; k <= Pieces; k++)
            {
                double t = (double)k / Pieces, t2 = t * t, t3 = t2 * t;
                double h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t, h01 = -2 * t3 + 3 * t2, h11 = t3 - t2;
                double x = h00 * p0.X + h10 * m0.X + h01 * p1.X + h11 * m1.X, y = h00 * p0.Y + h10 * m0.Y + h01 * p1.Y + h11 * m1.Y, z = h00 * p0.Z + h10 * m0.Z + h01 * p1.Z + h11 * m1.Z;
                // the direction of travel, to find the two banks
                double dx = (6 * t2 - 6 * t) * (p0.X - p1.X) + (3 * t2 - 4 * t + 1) * m0.X + (3 * t2 - 2 * t) * m1.X;
                double dy = (6 * t2 - 6 * t) * (p0.Y - p1.Y) + (3 * t2 - 4 * t + 1) * m0.Y + (3 * t2 - 2 * t) * m1.Y;
                double length = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1e-6), half = 500 * Math.Abs(w0 + (w1 - w0) * t);
                FVector left = ToWorld(p, x - dy / length * half, y + dx / length * half, z), right = ToWorld(p, x + dy / length * half, y - dx / length * half, z);
                if (lastLeft is { } ll && lastRight is { } lr)
                {
                    yield return new Triangle(ll, lr, right);
                    yield return new Triangle(ll, right, left);
                }
                (lastLeft, lastRight) = (left, right);
            }
        }
    }

    /// <summary>
    /// The outlines of the sea leave gaps far out. The sea reaches wherever the ground lies below its level
    /// and the way there is under water too.
    /// </summary>
    private static void FillSea(float[] surface, float[] ground, int width, int height, float seaLevel)
    {
        if (float.IsNaN(seaLevel))
            return;
        var open = new Queue<int>();
        for (int i = 0; i < ground.Length; i++)
            if (Math.Abs(surface[i] - seaLevel) < 100 && ground[i] < surface[i])
                open.Enqueue(i);
        while (open.TryDequeue(out int i))
        {
            int x = i % width, y = i / width;
            foreach (int n in (ReadOnlySpan<int>)[x > 0 ? i - 1 : -1, x + 1 < width ? i + 1 : -1, y > 0 ? i - width : -1, y + 1 < height ? i + width : -1])
            {
                if (n < 0 || !(ground[n] < seaLevel) || surface[n] >= seaLevel)
                    continue;
                surface[n] = seaLevel;
                open.Enqueue(n);
            }
        }
    }

    private static List<(FVector Out, FVector Arrive, FVector Leave)> SplinePoints(UObject spline, string curve) =>
        (spline.GetOrDefault<FStructFallback?>("SplineCurves")?.GetOrDefault<FStructFallback?>(curve)?.GetOrDefault<FStructFallback[]>("Points") ?? [])
        .Select(p => (p.GetOrDefault<FVector>("OutVal"), p.GetOrDefault<FVector>("ArriveTangent"), p.GetOrDefault<FVector>("LeaveTangent"))).ToList();

    /// <summary>Per sample of the map the height (cm) of the highest water surface above it; very low where there is none.</summary>
    private static float[] WaterSurface(IEnumerable<Triangle> water, int width, int height, double worldX, double worldY, double cell)
    {
        float[] surface = new float[width * height];
        Array.Fill(surface, float.MinValue);
        foreach (var t in water)
        {
            double ax = (t.A.X - worldX) / cell, ay = (t.A.Y - worldY) / cell, bx = (t.B.X - worldX) / cell, by = (t.B.Y - worldY) / cell, cx = (t.C.X - worldX) / cell, cy = (t.C.Y - worldY) / cell;
            double area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            if (Math.Abs(area) < 1e-9)
                continue;
            int x0 = Math.Max((int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))), 0), x1 = Math.Min((int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))), width - 1);
            int y0 = Math.Max((int)Math.Floor(Math.Min(ay, Math.Min(by, cy))), 0), y1 = Math.Min((int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))), height - 1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    double wa = ((bx - x) * (cy - y) - (cx - x) * (by - y)) / area, wb = ((cx - x) * (ay - y) - (ax - x) * (cy - y)) / area, wc = 1 - wa - wb;
                    if (wa < 0 || wb < 0 || wc < 0)
                        continue;
                    float z = (float)(wa * t.A.Z + wb * t.B.Z + wc * t.C.Z);
                    if (z > surface[y * width + x])
                        surface[y * width + x] = z;
                }
            }
        }
        return surface;
    }
}
