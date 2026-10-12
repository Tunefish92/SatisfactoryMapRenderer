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
        if (terrain)
            WriteFactory3D(world, outputDirectory, game);

        using var stream = File.Create(Path.Combine(outputDirectory, "map-data.js"));
        stream.Write("window.MAP_DATA = "u8);
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteData(w, world, tiles, maxZoom, icons, logo, terrain, terrain ? game?.Ground : null);
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
        CopyGround(game, Path.Combine(directory, GameGround.Directory));

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

    /// <summary>The game's ground textures and their map; only copied again when the game data changed.</summary>
    private static void CopyGround(GameAssets game, string directory)
    {
        if (game.Ground is null)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            return;
        }
        string info = Path.Combine(game.GroundDirectory, GameGround.InfoFile), copy = Path.Combine(directory, GameGround.InfoFile);
        string map = Path.Combine(game.GroundDirectory, GameGround.MapFile), mapCopy = Path.Combine(directory, GameGround.MapFile);
        if (File.Exists(copy) && File.Exists(mapCopy) && File.ReadAllText(copy) == File.ReadAllText(info) && new FileInfo(mapCopy).Length == new FileInfo(map).Length)
            return;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        foreach (string file in Directory.GetFiles(game.GroundDirectory))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
    }

    /// <summary>
    /// The factory for the 3D view (terrain/factory.js): every building as a box with an estimated height,
    /// belts, pipes, rails and power lines as chains of points at their real height. Numbers are whole
    /// centimetres to keep the file small.
    /// </summary>
    private static void WriteFactory3D(MapWorld world, string outputDirectory, GameAssets? game)
    {
        // The 3D models of the building types this save uses; everything else stays a box.
        var library = game?.Models;
        var classes = new Dictionary<string, int>(StringComparer.Ordinal);
        int Class(string name) => library is not null && library.Classes.ContainsKey(name)
            ? classes.TryGetValue(name, out int i) ? i : classes[name] = classes.Count
            : -1;

        bool IsLift(MapFeature f) => f.Shape == Shape.Box && f.Top is not null && library is not null && library.Lifts.ContainsKey(f.Class);
        var layers = new[] { Layers.Foundations, Layers.Buildings, Layers.Belts, Layers.Pipes, Layers.Railways, Layers.Power };
        var colors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Color(string color) => colors.TryGetValue(color, out int i) ? i : colors[color] = colors.Count;

        using var stream = File.Create(Path.Combine(outputDirectory, "terrain", "factory.js"));
        stream.Write("window.MAP_3D = "u8);
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartArray("layers");
            foreach (var layer in layers)
            {
                var features = world.In(layer).ToList();
                if (features.Count == 0)
                    continue;
                w.WriteStartObject();
                w.WriteString("id", layer.Id);
                w.WriteString("title", layer.Title);
                w.WriteBoolean("visible", layer.VisibleByDefault);

                // 16 numbers per building: position x y z, rotation quaternion x y z w (1/10000), scale x y z (1/1000),
                // then for its box: size x, size y, height, bottom relative to z; colour; model class or -1
                w.WriteStartArray("boxes");
                foreach (var f in features.Where(f => f.Shape == Shape.Box && !IsLift(f)))
                {
                    var category = Enum.TryParse<BuildCategory>(f.Group, out var parsed) ? parsed : BuildCategory.Other;
                    var (height, baseOffset) = Catalog.HeightOf(f.Class, category);
                    var r = f.Rotation;
                    foreach (double v in new[]
                             {
                                 f.Position.X, f.Position.Y, f.Position.Z, r.X * 10000, r.Y * 10000, r.Z * 10000, r.W * 10000,
                                 f.Scale.X * 1000, f.Scale.Y * 1000, f.Scale.Z * 1000,
                                 f.SizeX, f.SizeY, height, baseOffset, Color(f.Color), Class(f.Class),
                             })
                        w.WriteNumberValue((long)Math.Round(v));
                }
                w.WriteEndArray();

                // 16 numbers per conveyor lift: position x y z, rotation x y z w (1/10000), the other end's offset
                // x y z and rotation x y z w (1/10000), colour, model class
                w.WriteStartArray("lifts");
                foreach (var f in features.Where(IsLift))
                {
                    var (offset, turned) = f.Top!.Value;
                    var r = f.Rotation;
                    foreach (double v in new[]
                             {
                                 f.Position.X, f.Position.Y, f.Position.Z, r.X * 10000, r.Y * 10000, r.Z * 10000, r.W * 10000,
                                 offset.X, offset.Y, offset.Z, turned.X * 10000, turned.Y * 10000, turned.Z * 10000, turned.W * 10000,
                                 Color(f.Color), Class(f.Class),
                             })
                        w.WriteNumberValue((long)Math.Round(v));
                }
                w.WriteEndArray();

                // per line: colour, width, model class or -1, number of points, then x, y, z of each point
                w.WriteStartArray("lines");
                foreach (var f in features.Where(f => f.Shape == Shape.Line && f.Path is { Length: >= 4 } && f.PathZ?.Length == f.Path.Length / 2))
                {
                    w.WriteNumberValue(Color(f.Color));
                    w.WriteNumberValue((long)Math.Round(f.Thickness));
                    w.WriteNumberValue(library is not null && library.Splines.ContainsKey(f.Class) ? Class(f.Class) : -1);
                    w.WriteNumberValue(f.PathZ!.Length);
                    for (int i = 0; i < f.PathZ.Length; i++)
                    {
                        w.WriteNumberValue((long)Math.Round(f.Path![i * 2]));
                        w.WriteNumberValue((long)Math.Round(f.Path[i * 2 + 1]));
                        w.WriteNumberValue((long)Math.Round(f.PathZ[i]));
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            var players = world.In(Layers.Players).ToList();
            if (library is not null && library.Classes.ContainsKey(GameModels.PlayerClass) && players.Count > 0)
            {
                // The pioneers, standing where they were when the game was saved.
                w.WriteStartObject();
                w.WriteString("id", "players");
                w.WriteString("title", "Players");
                w.WriteBoolean("visible", true);
                w.WriteStartArray("boxes");
                foreach (var f in players)
                {
                    var r = f.Rotation;
                    foreach (double v in new[]
                             {
                                 f.Position.X, f.Position.Y, f.Position.Z, r.X * 10000, r.Y * 10000, r.Z * 10000, r.W * 10000,
                                 1000, 1000, 1000, 80, 80, 190, -95, Color("#ff4d4d"), Class(GameModels.PlayerClass),
                             })
                        w.WriteNumberValue((long)Math.Round(v));
                }
                w.WriteEndArray();
                w.WriteStartArray("lines");
                w.WriteEndArray();
                w.WriteEndObject();
            }
            if (library is { Resources.Count: > 0 })
            {
                // The rocks of the resource nodes are part of the world, the same in every save.
                w.WriteStartObject();
                w.WriteString("id", "resources");
                w.WriteString("title", "Resource nodes");
                w.WriteBoolean("visible", true);
                w.WriteStartArray("boxes");
                foreach (var r in library.Resources)
                {
                    foreach (double v in new[]
                             {
                                 r.T[0], r.T[1], r.T[2], r.R[0] * 10000, r.R[1] * 10000, r.R[2] * 10000, r.R[3] * 10000,
                                 r.S[0] * 1000, r.S[1] * 1000, r.S[2] * 1000, 100, 100, 100, 0, Color("#b4b9d6"), Class(r.Class),
                             })
                        w.WriteNumberValue((long)Math.Round(v));
                }
                w.WriteEndArray();
                w.WriteStartArray("lines");
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("colors");
            foreach (var color in colors.OrderBy(c => c.Value).Select(c => c.Key))
                w.WriteStringValue(color);
            w.WriteEndArray();

            if (library is not null && classes.Count > 0)
                WriteModels(w, library, classes, game!, Path.Combine(outputDirectory, "terrain"));
            w.WriteEndObject();
        }
        stream.Write(";\n"u8);
    }

    /// <summary>
    /// Copies what the save's buildings need out of the model library next to the map: the meshes into
    /// terrain/models.bin, the texture images into terrain/textures. Describes them for the viewer: per mesh
    /// where its data is and which material each run of indices has, per class which meshes it consists of.
    /// </summary>
    private static void WriteModels(Utf8JsonWriter w, ModelLibrary library, Dictionary<string, int> classes, GameAssets game, string terrainDirectory)
    {
        var meshes = new Dictionary<string, int>(StringComparer.Ordinal);
        var materials = new Dictionary<int, int>();
        using var source = File.OpenRead(game.ModelDataPath);
        using var output = File.Create(Path.Combine(terrainDirectory, "models.bin"));

        string textures = Path.Combine(terrainDirectory, ModelLibrary.TextureDirectory);
        if (Directory.Exists(textures))
            Directory.Delete(textures, recursive: true);
        Directory.CreateDirectory(textures);
        void CopyTexture(string file) => File.Copy(Path.Combine(game.ModelTextureDirectory, file), Path.Combine(textures, file), overwrite: true);

        w.WriteStartArray("meshes");
        foreach (var name in classes.Keys.SelectMany(c => library.Classes[c]).Select(p => p.Mesh).Distinct(StringComparer.Ordinal))
        {
            var mesh = library.Meshes[name];
            meshes[name] = meshes.Count;
            long vertexOffset = Copy(mesh.VertexOffset, mesh.V * (long)GameModels.VertexBytes);
            long indexOffset = Copy(mesh.IndexOffset, mesh.I * (mesh.Wide ? 4L : 2L));
            // vertex count, index count, wide indices, vertex offset, index offset, bounds min x y z, bounds size x y z,
            // then per section: first index, index count, material
            w.WriteStartArray();
            foreach (double v in new double[] { mesh.V, mesh.I, mesh.Wide ? 1 : 0, vertexOffset, indexOffset, mesh.Min[0], mesh.Min[1], mesh.Min[2], mesh.Size[0], mesh.Size[1], mesh.Size[2] })
                w.WriteNumberValue(v);
            foreach (var section in mesh.Sections)
            {
                w.WriteNumberValue(section[0]);
                w.WriteNumberValue(section[1]);
                w.WriteNumberValue(materials.TryGetValue(section[2], out int m) ? m : materials[section[2]] = materials.Count);
            }
            w.WriteEndArray();
        }
        w.WriteEndArray();

        // per material: kind, then the texture's file name or the colour, then the paint mask's file name if it has one
        w.WriteStartArray("materials");
        foreach (var material in materials.OrderBy(m => m.Value).Select(m => library.Materials[m.Key]))
        {
            w.WriteStartArray();
            w.WriteStringValue(material.Kind);
            w.WriteStringValue(material.Texture ?? material.Color ?? "");
            if (material.Mask is not null)
                w.WriteStringValue(material.Mask);
            w.WriteEndArray();
            if (material.Texture is not null)
                CopyTexture(material.Texture);
            if (material.Mask is not null)
                CopyTexture(material.Mask);
        }
        w.WriteEndArray();

        w.WriteStartArray("factoryLayers");
        if (materials.Keys.Any(m => library.Materials[m].Kind == "factory"))
        {
            foreach (string layer in library.FactoryLayers)
            {
                w.WriteStringValue(layer);
                CopyTexture(layer);
            }
        }
        w.WriteEndArray();

        // per class: 0, or for a class laid along a path the length of one piece of its mesh
        w.WriteStartArray("pieces");
        foreach (var name in classes.OrderBy(c => c.Value).Select(c => c.Key))
            w.WriteNumberValue(library.Splines.GetValueOrDefault(name) + library.Lifts.GetValueOrDefault(name));
        w.WriteEndArray();

        // per class: parts as mesh, translation x y z, rotation x y z w, scale x y z
        w.WriteStartArray("classes");
        foreach (var name in classes.OrderBy(c => c.Value).Select(c => c.Key))
        {
            w.WriteStartArray();
            foreach (var part in library.Classes[name])
            {
                w.WriteNumberValue(meshes[part.Mesh]);
                foreach (double v in part.T.Concat(part.R).Concat(part.S))
                    w.WriteNumberValue(v);
            }
            w.WriteEndArray();
        }
        w.WriteEndArray();

        long Copy(long offset, long length)
        {
            while (output.Position % 4 != 0)
                output.WriteByte(0);
            long at = output.Position;
            byte[] buffer = new byte[length];
            source.Position = offset;
            source.ReadExactly(buffer);
            output.Write(buffer);
            return at;
        }
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

    private static void WriteData(Utf8JsonWriter w, MapWorld world, IReadOnlyDictionary<string, TileSet> tiles, int maxZoom, Dictionary<string, int> icons, string? logo, bool terrain, GroundInfo? ground)
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
        if (ground is not null)
        {
            // The ground of the 3D view: terrain/ground/ground.png says which of these textures lies where.
            w.WriteStartObject("ground");
            w.WriteNumber("minX", ground.MinX);
            w.WriteNumber("minY", ground.MinY);
            w.WriteNumber("cell", ground.Cell);
            w.WriteNumber("tile", ground.Tile);
            w.WriteNumber("depthStep", ground.DepthStep);
            w.WriteStartArray("layers");
            foreach (string layer in ground.Layers)
                w.WriteStringValue(layer);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteString("renderedBy", Version);
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
        // After an update the server notes which version the map came from ("" if that was not recorded yet).
        string noted = Path.Combine(outputDirectory, UpdatedFromFile);
        string? updatedFrom = File.Exists(noted) ? File.ReadAllText(noted).Trim() : null;
        File.WriteAllText(Path.Combine(outputDirectory, "version.js"),
            $"window.MAP_RENDERER = {JsonSerializer.Serialize(new { version = Version, repository = Repository, updatedFrom, changes = Changes() })};\n");
    }

    /// <summary>
    /// The changelog for the "what is new" window of the viewer: the newest versions first, each with its
    /// sections (a title, possibly empty, and the entries under it).
    /// </summary>
    internal static List<object> Changes()
    {
        var versions = new List<(string Version, List<(string Title, List<string> Items)> Sections)>();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Changelog.md");
        if (stream is null)
            return [];
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                // "## 0.2.1 – 2026-10-12"
                versions.Add((line[3..].Split(' ')[0], []));
                continue;
            }
            if (versions.Count == 0 || line.Trim().Length == 0 || line.StartsWith("```", StringComparison.Ordinal))
                continue;
            var sections = versions[^1].Sections;
            if (line.StartsWith("### ", StringComparison.Ordinal))
                sections.Add((line[4..].Trim(), []));
            else if (line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (sections.Count == 0)
                    sections.Add(("", []));
                sections[^1].Items.Add(line[2..].Trim());
            }
            else if (sections.Count > 0 && sections[^1].Items.Count > 0)
                sections[^1].Items[^1] += " " + line.Trim(); // an entry that runs on over several lines
        }
        return [.. versions.Where(v => char.IsDigit(v.Version.FirstOrDefault())).Take(MaxChangelogVersions).Select(v => (object)new
        {
            version = v.Version,
            sections = v.Sections.Where(s => s.Items.Count > 0).Select(s => new { title = s.Title, items = s.Items }),
        })];
    }

    /// <summary>Written by the server into a map's folder when it finds a map of another version.</summary>
    public const string UpdatedFromFile = ".updated-from";

    /// <summary>The viewer only ever shows what is new since the last visit; older history is on GitHub.</summary>
    private const int MaxChangelogVersions = 8;

    /// <summary>GitHub repository whose releases the viewer checks for updates.</summary>
    public const string Repository = "Tunefish92/SatisfactoryMapRenderer";

    /// <summary>The renderer's version without build metadata, e.g. "0.1.0" or "0.2.0-beta.1".</summary>
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}
