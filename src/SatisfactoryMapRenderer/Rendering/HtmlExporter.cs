using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using SatisfactoryMapRenderer.Game;
using SatisfactoryMapRenderer.World;
using SkiaSharp;

namespace SatisfactoryMapRenderer.Rendering;

/// <summary>Writes the Leaflet viewer (index.html + assets) and the map-data.js it reads.</summary>
public static class HtmlExporter
{
    private static readonly LayerDef[] FactoryLayers = [Layers.Foundations, Layers.Buildings, Layers.Belts, Layers.Pipes, Layers.Railways];

    public static void Export(MapWorld world, IReadOnlyDictionary<string, TileSet> tiles, string outputDirectory, int maxZoom, GameAssets? game = null)
    {
        CopyAssets(outputDirectory);
        var icons = CopyIcons(world, outputDirectory, game);
        string? logo = CopyLogo(outputDirectory, game);
        bool terrain = CopyTerrain(outputDirectory, game);

        using var stream = File.Create(Path.Combine(outputDirectory, "map-data.js"));
        stream.Write("window.MAP_DATA = "u8);
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteData(w, world, tiles, maxZoom, icons, logo, terrain);
        stream.Write(";\n"u8);
    }

    /// <summary>Copies the PNG of every icon used by a marker to output/icons; returns icon key → index.</summary>
    private static Dictionary<string, int> CopyIcons(MapWorld world, string outputDirectory, GameAssets? game)
    {
        var icons = new Dictionary<string, int>(StringComparer.Ordinal);
        string iconDir = Path.Combine(outputDirectory, "icons");
        if (Directory.Exists(iconDir))
            Directory.Delete(iconDir, recursive: true);
        if (game is null)
            return icons;

        Directory.CreateDirectory(iconDir);
        foreach (var key in world.Features.Select(f => f.Icon).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            string source = game.IconPath(key);
            if (!File.Exists(source))
                continue;
            File.Copy(source, Path.Combine(iconDir, Path.GetFileName(source)), overwrite: true);
            icons[key] = icons.Count;
        }
        return icons;
    }

    private static string IconUrl(string key) => "icons/" + key.Replace('/', '-') + ".png";

    /// <summary>Brings the viewer files of an existing output up to date without touching its map.</summary>
    public static void RefreshViewer(string outputDirectory, GameAssets? game = null)
    {
        CopyAssets(outputDirectory);
        // Only replaces a logo the map already refers to; map-data.js is left as it is.
        if (File.Exists(Path.Combine(outputDirectory, GameLogoFile)))
            CopyLogo(outputDirectory, game);
    }

    /// <summary>The viewer without any save, for a served map that waits for one to be chosen.</summary>
    public static void ExportEmpty(string outputDirectory, int maxZoom, GameAssets? game = null)
    {
        Directory.CreateDirectory(outputDirectory);
        CopyAssets(outputDirectory);
        string? logo = CopyLogo(outputDirectory, game);
        using var stream = File.Create(Path.Combine(outputDirectory, "map-data.js"));
        stream.Write("window.MAP_DATA = "u8);
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("title", "Satisfactory Map");
            w.WriteBoolean("empty", true);
            w.WriteString("fileName", "");
            w.WriteNumber("maxZoom", maxZoom);
            if (logo is not null)
                w.WriteString("logo", logo);
            w.WriteStartObject("world");
            w.WriteNumber("minX", WorldBounds.MinX);
            w.WriteNumber("minY", WorldBounds.MinY);
            w.WriteNumber("size", WorldBounds.Size);
            w.WriteEndObject();
            foreach (string name in new[] { "summary", "counts", "icons", "layers", "inspect" })
            {
                w.WriteStartArray(name);
                w.WriteEndArray();
            }
            w.WriteStartObject("markers");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        stream.Write(";\n"u8);
    }

    /// <summary>
    /// Files of the 3D view (terrain.html): the world's heights and the in-game map as its texture, in two
    /// sizes so that weaker graphics hardware is not asked for the large one. Returns false without them.
    /// </summary>
    private static bool CopyTerrain(string outputDirectory, GameAssets? game)
    {
        string directory = Path.Combine(outputDirectory, "terrain");
        if (game?.TerrainHeightsPath is not { } heights || game.MapImagePath is not { } map)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            return false;
        }

        Directory.CreateDirectory(directory);
        File.Copy(heights, Path.Combine(directory, "heights.png"), overwrite: true);
        File.Copy(game.TerrainInfoPath, Path.Combine(directory, "terrain.json"), overwrite: true);

        // The textures only change with the game data; cutting them again on every render would be wasted time.
        string stamp = Path.Combine(directory, ".source"), wanted = $"{new FileInfo(map).Length}|{File.GetLastWriteTimeUtc(map).Ticks}";
        if (File.Exists(stamp) && File.ReadAllText(stamp) == wanted && File.Exists(Path.Combine(directory, "map-8k.jpg")) && File.Exists(Path.Combine(directory, "map-4k.jpg")))
            return true;
        using var image = SKBitmap.Decode(map);
        if (image is null)
            return false;
        foreach (var (name, size) in new[] { ("map-8k.jpg", 8192), ("map-4k.jpg", 4096) })
        {
            using var scaled = image.Width == size ? null : image.Resize(new SKImageInfo(size, size), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var stream = File.Create(Path.Combine(directory, name));
            (scaled ?? image).Encode(stream, SKEncodedImageFormat.Jpeg, 88);
        }
        File.WriteAllText(stamp, wanted);
        return true;
    }

    private const string GameLogoFile = "game-logo.png";

    /// <summary>The game's icon replaces the viewer's own logo when the game data has one.</summary>
    private static string? CopyLogo(string outputDirectory, GameAssets? game)
    {
        string target = Path.Combine(outputDirectory, GameLogoFile);
        if (game?.LogoPath is not { } source)
        {
            File.Delete(target);
            return null;
        }
        using var icon = SKBitmap.Decode(source);
        if (icon is null)
        {
            File.Delete(target);
            return null;
        }
        using var logo = AddMapPin(icon);
        using (var stream = File.Create(target))
            logo.Encode(stream, SKEncodedImageFormat.Png, 100);
        return GameLogoFile;
    }

    /// <summary>Marks the game's icon as "the map": a location pin in the viewer's accent colours, bottom right.</summary>
    private static SKBitmap AddMapPin(SKBitmap icon)
    {
        const int size = 256;
        var logo = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(logo);
        canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(icon);
        canvas.DrawImage(image, new SKRect(0, 0, size, size), new SKSamplingOptions(SKCubicResampler.Mitchell));

        // The pin of the bundled logo (logo.svg, 64-unit grid), with its tip at the lower right.
        using var pin = SKPath.ParseSvgPathData("M32 9 a10 10 0 0 1 10 10 c0 7 -7.2 12.6 -10 17 c-2.8 -4.4 -10 -10 -10 -17 a10 10 0 0 1 10 -10 Z");
        const float scale = 5.0f, tipX = 192, tipY = 250;
        pin.Transform(SKMatrix.CreateScale(scale, scale).PostConcat(SKMatrix.CreateTranslation(tipX - 32 * scale, tipY - 36 * scale)));

        using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 110), ImageFilter = SKImageFilter.CreateBlur(7, 7) };
        canvas.Save();
        canvas.Translate(0, 5);
        canvas.DrawPath(pin, shadow);
        canvas.Restore();

        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 12, StrokeJoin = SKStrokeJoin.Round, Color = SKColors.White };
        canvas.DrawPath(pin, outline);
        var bounds = pin.Bounds;
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(bounds.Left, bounds.Top), new SKPoint(bounds.Right, bounds.Bottom),
                [SKColor.Parse("#2563eb"), SKColor.Parse("#06b6d4")], SKShaderTileMode.Clamp),
        };
        canvas.DrawPath(pin, fill);
        using var dot = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawCircle(tipX, tipY - 17 * scale, 4.4f * scale, dot);
        return logo;
    }

    private static void WriteData(Utf8JsonWriter w, MapWorld world, IReadOnlyDictionary<string, TileSet> tiles, int maxZoom, Dictionary<string, int> icons, string? logo, bool terrain)
    {
        int IconIndex(string? key) => key is not null && icons.TryGetValue(key, out int i) ? i : -1;
        var header = world.Save.Header;
        w.WriteStartObject();
        w.WriteString("title", header.SessionName.Length > 0 ? header.SessionName : Path.GetFileNameWithoutExtension(world.Save.FilePath));
        w.WriteString("fileName", Path.GetFileName(world.Save.FilePath));
        w.WriteNumber("maxZoom", maxZoom);
        if (logo is not null)
            w.WriteString("logo", logo);
        if (terrain)
            w.WriteBoolean("terrain", true);
        if (header.SaveName.Length > 0)
            w.WriteString("saveName", header.SaveName);
        if (header.SaveDateTimeUtc != default)
            w.WriteString("savedAt", header.SaveDateTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));

        w.WriteStartObject("world");
        w.WriteNumber("minX", WorldBounds.MinX);
        w.WriteNumber("minY", WorldBounds.MinY);
        w.WriteNumber("size", WorldBounds.Size);
        w.WriteEndObject();

        if (FocusBounds(world) is { } focus)
        {
            w.WriteStartArray("focus");
            foreach (double v in focus)
                w.WriteNumberValue(Math.Round(v));
            w.WriteEndArray();
        }

        WritePairs(w, "summary", world.Summary.Select(p => (p.Key, (object)p.Value)));
        WritePairs(w, "counts", world.Counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key).Select(c => (c.Key, (object)c.Value)));

        w.WriteStartArray("icons");
        foreach (var key in icons.OrderBy(i => i.Value).Select(i => i.Key))
            w.WriteStringValue(IconUrl(key));
        w.WriteEndArray();

        var byLayer = world.Features.GroupBy(f => f.Layer).ToDictionary(g => g.Key, g => g.ToList());
        var legendIndex = new Dictionary<LayerDef, Dictionary<string, int>>();

        w.WriteStartArray("layers");
        foreach (var layer in Layers.All)
        {
            var features = byLayer.GetValueOrDefault(layer) ?? [];
            var groups = features
                .GroupBy(f => f.Group ?? layer.Title)
                .Select(g => (Label: g.Key, Color: g.First().Color, Count: g.Count(), Icon: IconIndex(g.Select(f => f.Icon).FirstOrDefault(i => i is not null))));
            // Resource types read best alphabetically (Iron Ore · Impure, · Normal, · Pure); everything else by frequency.
            var legend = (layer == Layers.Resources ? groups.OrderBy(g => g.Label, StringComparer.Ordinal) : groups.OrderByDescending(g => g.Count)).ToList();
            legendIndex[layer] = legend.Select((g, i) => (g.Label, i)).ToDictionary(x => x.Label, x => x.i);

            w.WriteStartObject();
            w.WriteString("id", layer.Id);
            w.WriteString("title", layer.Title);
            w.WriteString("group", layer.Group);
            w.WriteString("kind", layer.Kind == LayerKind.Raster ? "raster" : "marker");
            w.WriteBoolean("visible", layer.VisibleByDefault);
            w.WriteString("color", layer.Color);
            w.WriteString("description", layer.Description);
            w.WriteNumber("count", layer == Layers.Explored || layer == Layers.Background || layer == Layers.FogOfWar ? -1 : features.Count);

            w.WriteStartArray("legend");
            foreach (var (label, color, count, icon) in legend)
            {
                w.WriteStartArray();
                w.WriteStringValue(label);
                w.WriteStringValue(color);
                w.WriteNumberValue(count);
                w.WriteNumberValue(icon);
                w.WriteEndArray();
            }
            w.WriteEndArray();

            if (tiles.TryGetValue(layer.Id, out var tileSet))
            {
                w.WriteStartObject("tiles");
                w.WriteString("ext", tileSet.Extension);
                w.WriteNumber("maxNativeZoom", tileSet.MaxNativeZoom);
                w.WriteStartArray("list");
                foreach (var tile in tileSet.Tiles.Order())
                    w.WriteStringValue(tile);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartObject("markers");
        foreach (var layer in Layers.All.Where(l => l.Kind == LayerKind.Marker))
        {
            w.WriteStartArray(layer.Id);
            foreach (var f in byLayer.GetValueOrDefault(layer) ?? [])
            {
                w.WriteStartArray();
                w.WriteNumberValue(Math.Round(f.Position.X));
                w.WriteNumberValue(Math.Round(f.Position.Y));
                w.WriteNumberValue(Math.Round(f.Position.Z));
                w.WriteStringValue(f.Name);
                w.WriteStringValue(f.Color);
                w.WriteNumberValue(legendIndex[layer].GetValueOrDefault(f.Group ?? layer.Title));
                w.WriteNumberValue(f.Dimmed ? 1 : 0);
                WriteDetails(w, f.Details);
                w.WriteNumberValue(IconIndex(f.Icon));
                w.WriteStringValue(f.Ring ?? "");
                w.WriteNumberValue(f.Badge ? 1 : 0);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();

        // Buildings are raster tiles; this list makes them clickable.
        w.WriteStartArray("inspect");
        foreach (var f in byLayer.GetValueOrDefault(Layers.Buildings) ?? [])
        {
            if (f.Details is null)
                continue;
            w.WriteStartArray();
            w.WriteNumberValue(Math.Round(f.Position.X));
            w.WriteNumberValue(Math.Round(f.Position.Y));
            w.WriteNumberValue(Math.Round(Math.Max(f.SizeX, f.SizeY) / 2));
            w.WriteStringValue(f.Name);
            w.WriteStringValue(f.Color);
            w.WriteStringValue(f.Group);
            WriteDetails(w, f.Details);
            w.WriteNumberValue(IconIndex(f.Icon));
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteDetails(Utf8JsonWriter w, List<KeyValuePair<string, string>>? details)
    {
        if (details is not { Count: > 0 })
        {
            w.WriteNumberValue(0);
            return;
        }
        w.WriteStartArray();
        foreach (var (key, value) in details)
        {
            w.WriteStartArray();
            w.WriteStringValue(key);
            w.WriteStringValue(value);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    private static void WritePairs(Utf8JsonWriter w, string name, IEnumerable<(string Key, object Value)> pairs)
    {
        w.WriteStartArray(name);
        foreach (var (key, value) in pairs)
        {
            w.WriteStartArray();
            w.WriteStringValue(key);
            if (value is int i)
                w.WriteNumberValue(i);
            else
                w.WriteStringValue(value.ToString());
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    /// <summary>Area to show initially: the factory, or the players if nothing is built yet.</summary>
    private static double[]? FocusBounds(MapWorld world)
    {
        var points = world.Features.Where(f => FactoryLayers.Contains(f.Layer)).Select(f => f.Position).ToList();
        if (points.Count == 0)
            points = world.Features.Where(f => f.Layer == Layers.Players).Select(f => f.Position).ToList();
        if (points.Count == 0)
            return null;

        // Ignore stray outliers (e.g. a lone pole far away) by using the 1st/99th percentile.
        var xs = points.Select(p => p.X).Order().ToArray();
        var ys = points.Select(p => p.Y).Order().ToArray();
        double Pct(double[] v, double q) => v[(int)Math.Clamp(Math.Round(q * (v.Length - 1)), 0, v.Length - 1)];
        double minX = Pct(xs, 0.01), maxX = Pct(xs, 0.99), minY = Pct(ys, 0.01), maxY = Pct(ys, 0.99);
        const double minimumSpan = 40000;
        if (maxX - minX < minimumSpan) { double c = (minX + maxX) / 2; minX = c - minimumSpan / 2; maxX = c + minimumSpan / 2; }
        if (maxY - minY < minimumSpan) { double c = (minY + maxY) / 2; minY = c - minimumSpan / 2; maxY = c + minimumSpan / 2; }
        return [minX, minY, maxX, maxY];
    }

    private static void CopyAssets(string outputDirectory)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            string name = resource.Replace('\\', '/');
            if (!name.StartsWith("Assets/", StringComparison.Ordinal))
                continue;
            // Assets/viewer/* goes to the root, Assets/leaflet/* to leaflet/.
            string relative = name["Assets/".Length..];
            if (relative.StartsWith("viewer/", StringComparison.Ordinal))
                relative = relative["viewer/".Length..];
            string target = Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = assembly.GetManifestResourceStream(resource)!;
            using var file = File.Create(target);
            source.CopyTo(file);
        }

        // Which renderer wrote the viewer, for the footer and the update check against the project's releases.
        File.WriteAllText(Path.Combine(outputDirectory, "version.js"),
            $"window.MAP_RENDERER = {JsonSerializer.Serialize(new { version = Version, repository = Repository })};\n");
    }

    /// <summary>GitHub repository whose releases the viewer checks for updates.</summary>
    public const string Repository = "Tunefish92/SatisfactoryMapRenderer";

    /// <summary>The renderer's version without build metadata, e.g. "0.1.0" or "0.2.0-beta.1".</summary>
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}
