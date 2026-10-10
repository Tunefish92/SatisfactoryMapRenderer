using System.Text.Json;
using SatisfactoryMapRenderer.Rendering;
using SatisfactoryMapRenderer.Save;
using SatisfactoryMapRenderer.Tests.TestSupport;
using SatisfactoryMapRenderer.World;

namespace SatisfactoryMapRenderer.Tests;

/// <summary>Save → map features → tiles and viewer.</summary>
public sealed class MapPipelineTests
{
    private static MapWorld Extract(int headerVersion, int saveVersion)
    {
        string path = TestScene.Create(headerVersion, saveVersion).WriteToTempFile();
        try
        {
            return WorldExtractor.Extract(SaveGameReader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(SaveFormatTests.Versions), MemberType = typeof(SaveFormatTests))]
    public void Extracts_map_features(int headerVersion, int saveVersion)
    {
        var world = Extract(headerVersion, saveVersion);

        var constructor = Assert.Single(world.In(Layers.Buildings), f => f.Name == "Constructor");
        Assert.Equal(Shape.Box, constructor.Shape);
        Assert.Equal(Math.PI / 2, constructor.Yaw, 4);
        Assert.Contains(new KeyValuePair<string, string>("Recipe", "Iron Plate"), constructor.Details!);
        Assert.Contains(new KeyValuePair<string, string>("Clock speed", "150 %"), constructor.Details!);

        // The belt spline (local 0→1000 along X) is placed at the actor position.
        var belt = Assert.Single(world.In(Layers.Belts));
        Assert.Equal(Shape.Line, belt.Shape);
        Assert.Equal([5000, 5000], belt.Path![..2]);
        Assert.Equal([6000, 5000], belt.Path![^2..]);
        Assert.Contains(world.Summary, s => s is { Key: "Conveyor belts", Value: "0.01 km" });

        // The node's resource type is inferred from the miner standing on it.
        var node = Assert.Single(world.In(Layers.Resources));
        Assert.Equal("Iron Ore", node.Group);
        Assert.False(node.Dimmed);
        Assert.Contains(new KeyValuePair<string, string>("Occupied", "Miner Mk.1"), node.Details!);

        var wire = Assert.Single(world.In(Layers.Power), f => f.Shape == Shape.Line);
        Assert.Equal([0, 10000, 10000, 10000], wire.Path!);

        var player = Assert.Single(world.In(Layers.Players));
        Assert.Equal("Pioneer Ünicode", player.Name);
        Assert.Equal(new Vector3(100, 200, 300), player.Position);

        Assert.Equal("Central Station", Assert.Single(world.In(Layers.Stations)).Name);
        Assert.Equal("Blue Power Slug", Assert.Single(world.In(Layers.Slugs)).Name);
        Assert.All(world.In(Layers.Foliage), f => Assert.Equal("Trees", f.Group));
        Assert.Equal(2, world.In(Layers.Foliage).Count());
        Assert.Equal(4, world.FogOfWarSize);
        Assert.Contains(world.Summary, s => s is { Key: "Game phase", Value: "Phase 2 (Construction Dock)" });

        int lightweight = world.In(Layers.Foundations).Count(f => f.Class == "Build_Foundation_8x1_01_C");
        Assert.Equal(saveVersion >= 46 ? 2 : 0, lightweight);
    }

    [Fact]
    public void Exports_viewer_tiles_and_data()
    {
        var world = Extract(14, 58);
        string output = Path.Combine(Path.GetTempPath(), $"smr-map-{Guid.NewGuid():N}");
        try
        {
            MapExporter.Export(world, new ExportOptions(output, MaxZoom: 4, BackgroundImage: null), _ => { });

            foreach (var file in new[] { "index.html", "viewer.js", "viewer.css", "map-data.js", "leaflet/leaflet.js", "leaflet/leaflet.css" })
                Assert.True(File.Exists(Path.Combine(output, file)), $"{file} missing");

            string js = File.ReadAllText(Path.Combine(output, "map-data.js"));
            Assert.StartsWith("window.MAP_DATA = ", js);
            using var data = JsonDocument.Parse(js["window.MAP_DATA = ".Length..].TrimEnd().TrimEnd(';'));
            var root = data.RootElement;

            Assert.Equal("Synthetic Session", root.GetProperty("title").GetString());
            Assert.Equal(1, root.GetProperty("markers").GetProperty("players").GetArrayLength());
            Assert.Equal(1, root.GetProperty("inspect").EnumerateArray().Count(i => i[3].GetString() == "Constructor"));

            // Every tile listed in the data exists on disk, and nothing else was written.
            var layers = root.GetProperty("layers").EnumerateArray().ToDictionary(l => l.GetProperty("id").GetString()!);
            int listed = 0;
            foreach (var (id, layer) in layers)
            {
                if (!layer.TryGetProperty("tiles", out var tiles))
                    continue;
                string ext = tiles.GetProperty("ext").GetString()!;
                foreach (var tile in tiles.GetProperty("list").EnumerateArray())
                {
                    listed++;
                    Assert.True(File.Exists(Path.Combine(output, "tiles", id, $"{tile.GetString()}.{ext}")), $"{id}/{tile} missing");
                }
            }
            Assert.Equal(listed, Directory.GetFiles(Path.Combine(output, "tiles"), "*.*", SearchOption.AllDirectories).Length);
            Assert.True(layers["belts"].TryGetProperty("tiles", out _));
            Assert.True(layers["explored"].TryGetProperty("tiles", out _));
            Assert.False(layers["background"].TryGetProperty("tiles", out _));
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Belt_is_drawn_into_the_tile_containing_it()
    {
        var world = Extract(14, 58);
        string output = Path.Combine(Path.GetTempPath(), $"smr-map-{Guid.NewGuid():N}");
        try
        {
            MapExporter.Export(world, new ExportOptions(output, MaxZoom: 7, BackgroundImage: null), _ => { });

            // Belt runs from world (5000, 5000) to (6000, 5000); at zoom 7 one pixel is ~22.9 units.
            double unitsPerPixel = WorldBounds.Size / (256 << 7);
            double px = (5500 - WorldBounds.MinX) / unitsPerPixel, py = (5000 - WorldBounds.MinY) / unitsPerPixel;
            string tile = Path.Combine(output, "tiles", "belts", "7", ((int)(px / 256)).ToString(), $"{(int)(py / 256)}.png");
            Assert.True(File.Exists(tile));

            using var bitmap = SkiaSharp.SKBitmap.Decode(tile);
            var pixel = bitmap.GetPixel((int)(px % 256), (int)(py % 256));
            Assert.True(pixel.Alpha > 200, $"expected an opaque belt pixel, got {pixel}");
            Assert.True(pixel.Red > pixel.Blue, "belt should be drawn in its orange tier colour");
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }
}
