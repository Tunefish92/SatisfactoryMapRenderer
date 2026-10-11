using System.Collections.Concurrent;
using System.Diagnostics;
using SatisfactoryMapRenderer.Game;
using SatisfactoryMapRenderer.World;
using SkiaSharp;

namespace SatisfactoryMapRenderer.Rendering;

/// <param name="BackgroundImage">Explicit map image; otherwise the game's own map is used when available.</param>
public sealed record ExportOptions(string OutputDirectory, int MaxZoom, string? BackgroundImage, GameAssets? Game = null, string? RealisticImage = null, string? ReliefImage = null, string? GroundImage = null);

/// <summary>Renders all tile layers and writes the HTML viewer.</summary>
public static class MapExporter
{
    private const int FogMaxZoom = 4;

    public static void Export(MapWorld world, ExportOptions options, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(options.OutputDirectory);

        // Tiles from an earlier run would otherwise linger as stale files. The base maps do not depend on
        // the save and take the longest to cut, so they are kept while their image is unchanged.
        string tilesDir = Path.Combine(options.OutputDirectory, "tiles");
        if (Directory.Exists(tilesDir))
            foreach (string dir in Directory.GetDirectories(tilesDir))
                if (!BaseMaps.Any(l => l.Id == Path.GetFileName(dir)))
                    Directory.Delete(dir, recursive: true);

        var renderer = new TileRenderer(options.OutputDirectory, options.MaxZoom, log);
        var tiles = new Dictionary<string, TileSet>();

        string? backgroundPath = options.BackgroundImage ?? options.Game?.MapImagePath;
        string? reliefPath = options.ReliefImage ?? options.Game?.ReliefImagePath;
        string? groundPath = options.GroundImage ?? options.Game?.GroundMapPath;
        foreach (var (layer, image) in new[] { (Layers.Ground, groundPath), (Layers.Background, backgroundPath), (Layers.Relief, reliefPath), (Layers.Realistic, options.RealisticImage) })
        {
            string layerDir = Path.Combine(tilesDir, layer.Id);
            if (image is null)
            {
                if (Directory.Exists(layerDir))
                    Directory.Delete(layerDir, recursive: true);
                continue;
            }
            tiles[layer.Id] = ExistingBaseMap(layer, image, layerDir, options.MaxZoom, log) ?? RenderBaseMap(renderer, layer, image, layerDir, options.MaxZoom);
        }

        if (world.FogOfWar is { } fog)
        {
            // Over a real map the fog darkens unexplored land; without one it draws the explored outline.
            var layer = backgroundPath is null ? Layers.Explored : Layers.FogOfWar;
            using var fogImage = backgroundPath is null ? CreateExploredImage(fog, world.FogOfWarSize) : CreateFogImage(fog, world.FogOfWarSize);
            tiles[layer.Id] = renderer.RenderImage(layer, fogImage, Math.Min(FogMaxZoom, options.MaxZoom), "png");
        }

        // Building icons drawn into the tiles; decoded once and shared by all tile threads.
        var iconImages = new ConcurrentDictionary<string, SKImage?>(StringComparer.Ordinal);
        Func<string, SKImage?>? icons = options.Game is { } game
            ? key => iconImages.GetOrAdd(key, k => File.Exists(game.IconPath(k)) ? SKImage.FromEncodedData(game.IconPath(k))?.ToRasterImage(true) : null)
            : null;

        foreach (var layer in Layers.All.Where(l => l.Kind == LayerKind.Raster && !BaseMaps.Contains(l) && l != Layers.Explored && l != Layers.FogOfWar))
        {
            var features = world.In(layer).ToList();
            if (features.Count > 0)
                tiles[layer.Id] = renderer.RenderFeatures(layer, features, icons);
        }

        HtmlExporter.Export(world, tiles, options.OutputDirectory, options.MaxZoom, options.Game);
        foreach (var image in iconImages.Values)
            image?.Dispose();
        log($"Rendered {tiles.Values.Sum(t => t.Tiles.Count):N0} tiles in {sw.Elapsed.TotalSeconds:0.0}s");
    }

    /// <summary>Base maps cut from an image; they do not depend on the save and are kept between renders.</summary>
    private static readonly LayerDef[] BaseMaps = [Layers.Ground, Layers.Background, Layers.Relief, Layers.Realistic];

    private const string SourceStamp = ".source";

    /// <summary>Identifies the image a base map was cut from.</summary>
    private static string Stamp(string image, int maxZoom)
    {
        var info = new FileInfo(image);
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{maxZoom}";
    }

    private static TileSet RenderBaseMap(TileRenderer renderer, LayerDef layer, string image, string layerDir, int maxZoom)
    {
        if (Directory.Exists(layerDir))
            Directory.Delete(layerDir, recursive: true);
        var tileSet = renderer.RenderImageFile(layer, image, "jpg");
        File.WriteAllText(Path.Combine(layerDir, SourceStamp), Stamp(image, maxZoom));
        return tileSet;
    }

    /// <summary>The tiles already on disk, if they were cut from this very image.</summary>
    private static TileSet? ExistingBaseMap(LayerDef layer, string image, string layerDir, int maxZoom, Action<string> log)
    {
        string stampFile = Path.Combine(layerDir, SourceStamp);
        if (!File.Exists(stampFile) || File.ReadAllText(stampFile) != Stamp(image, maxZoom))
            return null;
        var tiles = new List<string>();
        int nativeZoom = 0;
        foreach (string file in Directory.EnumerateFiles(layerDir, "*.jpg", SearchOption.AllDirectories))
        {
            string[] parts = Path.GetRelativePath(layerDir, file).Split(Path.DirectorySeparatorChar);
            if (parts.Length != 3 || !int.TryParse(parts[0], out int z))
                continue;
            nativeZoom = Math.Max(nativeZoom, z);
            tiles.Add($"{parts[0]}/{parts[1]}/{Path.GetFileNameWithoutExtension(parts[2])}");
        }
        if (tiles.Count == 0)
            return null;
        log($"  {layer.Title}: {tiles.Count:N0} tiles (unchanged, kept)");
        return new TileSet(layer.Id, nativeZoom, "jpg", tiles);
    }

    /// <summary>The fog of war stores exploration (0–255) in the third byte of each pixel.</summary>
    private static SKBitmap CreateExploredImage(byte[] fog, int size) => FogBitmap(fog, size, SKColor.Parse(Layers.Explored.Color), invert: false);

    /// <summary>Dark veil over everything that is not explored yet.</summary>
    private static SKBitmap CreateFogImage(byte[] fog, int size) => FogBitmap(fog, size, SKColor.Parse(Layers.FogOfWar.Color), invert: true);

    private static SKBitmap FogBitmap(byte[] fog, int size, SKColor land, bool invert)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = bitmap.GetPixelSpan();
        for (int i = 0; i < size * size; i++)
        {
            byte coverage = invert ? (byte)(255 - fog[i * 4 + 2]) : fog[i * 4 + 2];
            pixels[i * 4 + 0] = land.Red;
            pixels[i * 4 + 1] = land.Green;
            pixels[i * 4 + 2] = land.Blue;
            pixels[i * 4 + 3] = (byte)(coverage * 0.85);
        }
        return bitmap;
    }
}
