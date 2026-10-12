using System.Collections.Concurrent;
using SatisfactoryMapRenderer.World;
using SkiaSharp;

namespace SatisfactoryMapRenderer.Rendering;

/// <param name="Path">
/// For a base map that was cut into tiles beforehand: where the viewer finds them (every tile down to
/// <paramref name="MaxNativeZoom"/> exists). Null for tiles written into the map's own folder.
/// </param>
public sealed record TileSet(string LayerId, int MaxNativeZoom, string Extension, IReadOnlyCollection<string> Tiles, string? Path = null);

/// <summary>
/// Renders map features into a Leaflet/XYZ tile pyramid. Zoom 0 is one 256 px tile covering <see cref="WorldBounds"/>.
/// Only tiles that contain something are written.
/// </summary>
public sealed class TileRenderer(string outputDirectory, int maxZoom, Action<string> log)
{
    public const int TileSize = 256;
    private const double CellSize = 10000; // spatial index cell (100 m)

    /// <param name="icons">Icon key → image; boxes with an icon get it drawn on top once they are large enough.</param>
    public TileSet RenderFeatures(LayerDef layer, IReadOnlyList<MapFeature> features, Func<string, SKImage?>? icons = null)
    {
        var index = new SpatialGrid(features);
        var tiles = new ConcurrentBag<string>();

        for (int z = 0; z <= maxZoom; z++)
        {
            double tileWorld = WorldBounds.Size / (1 << z);
            double unitsPerPixel = tileWorld / TileSize;
            double margin = 6 * unitsPerPixel; // minimum pixel sizes can grow features beyond their true bounds
            var candidates = index.CandidateTiles(z, tileWorld, margin);
            int zoom = z;

            Parallel.ForEach(candidates, () => new QueryState(features.Count), (tile, _, state) =>
            {
                double minX = WorldBounds.MinX + tile.X * tileWorld;
                double minY = WorldBounds.MinY + tile.Y * tileWorld;
                var hits = index.Query(minX - margin, minY - margin, minX + tileWorld + margin, minY + tileWorld + margin, state);
                if (hits.Count == 0)
                    return state;
                hits.Sort();

                using var bitmap = new SKBitmap(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    var painter = new FeaturePainter(canvas, minX, minY, unitsPerPixel);
                    foreach (int i in hits)
                        painter.Draw(features[i]);
                    if (icons is not null)
                        foreach (int i in hits)
                            if (features[i] is { Shape: Shape.Box, Icon: { } key } f && icons(key) is { } icon)
                                painter.DrawIcon(f, icon);
                    painter.Dispose();
                }
                Save(bitmap, layer.Id, zoom, tile.X, tile.Y, SKEncodedImageFormat.Png, "png");
                tiles.Add($"{zoom}/{tile.X}/{tile.Y}");
                return state;
            }, _ => { });
        }

        log($"  {layer.Title}: {features.Count:N0} features → {tiles.Count:N0} tiles");
        return new TileSet(layer.Id, maxZoom, "png", tiles.ToArray());
    }

    /// <summary>Images above this size are read in strips; Skia cannot hold more than 2 GB in one bitmap.</summary>
    private const long MaxWholeImageBytes = 1L << 30;

    /// <summary>Renders an image file that covers exactly the world bounds, down to its own resolution.</summary>
    public TileSet RenderImageFile(LayerDef layer, string path, string extension)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidOperationException($"Could not read image '{path}'.");
        int width = codec.Info.Width, height = codec.Info.Height;
        int nativeZoom = Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(width, height) / (double)TileSize)), 0, maxZoom);
        if ((long)width * height * 4 <= MaxWholeImageBytes)
        {
            using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidOperationException($"Could not read image '{path}'.");
            return RenderImage(layer, bitmap, nativeZoom, extension);
        }

        // Too large for one bitmap: the deepest zoom level is cut from strips of the image while a reduced
        // copy is assembled for the levels above it.
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (codec.StartScanlineDecode(info) != SKCodecResult.Success || codec.ScanlineOrder != SKCodecScanlineOrder.TopDown)
            throw new InvalidOperationException($"'{path}' is too large ({width}×{height}) to be read in one piece and its format cannot be read in strips; use a JPEG or a non-interlaced PNG.");

        var tiles = new ConcurrentBag<string>();
        var format = extension == "jpg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        var reduceSampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        int count = 1 << nativeZoom;
        double srcW = (double)width / count, srcH = (double)height / count;
        int reduction = 2;
        while ((long)(width / reduction) * (height / reduction) * 4 > MaxWholeImageBytes)
            reduction *= 2;
        using var reduced = new SKBitmap((width + reduction - 1) / reduction, (height + reduction - 1) / reduction, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var reducedCanvas = new SKCanvas(reduced);
        reducedCanvas.Clear(SKColors.Transparent);

        int rowsPerStrip = Math.Max(1, (int)(2048 / srcH));
        using var strip = new SKBitmap(width, (int)Math.Ceiling(rowsPerStrip * srcH) + 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        int decoded = 0;        // image rows read so far
        int stripTop = 0, stripRows = 0;
        for (int row0 = 0; row0 < count; row0 += rowsPerStrip)
        {
            int row1 = Math.Min(count, row0 + rowsPerStrip);
            int top = (int)Math.Floor(row0 * srcH), bottom = Math.Min(height, (int)Math.Ceiling(row1 * srcH));
            // Strips can share a row when tile edges fall between pixels; scanlines can only be read once.
            int carry = decoded - top;
            var pixels = strip.GetPixelSpan();
            if (carry > 0)
                pixels.Slice((stripRows - carry) * strip.RowBytes, carry * strip.RowBytes).CopyTo(pixels);
            int lines = bottom - decoded;
            if (codec.GetScanlines(strip.GetPixels() + carry * strip.RowBytes, lines, strip.RowBytes) != lines)
                throw new InvalidOperationException($"'{path}' ended unexpectedly at row {decoded}.");
            decoded = bottom;
            stripTop = top;
            stripRows = bottom - top;

            using var stripImage = SKImage.FromPixels(new SKImageInfo(width, stripRows, SKColorType.Rgba8888, SKAlphaType.Premul), strip.GetPixels(), strip.RowBytes);
            reducedCanvas.DrawImage(stripImage, new SKRect(0, 0, width, stripRows),
                new SKRect(0, (float)stripTop / reduction, (float)width / reduction, (float)bottom / reduction), reduceSampling);

            int firstRow = row0, offset = stripTop;
            Parallel.For(0, (row1 - row0) * count, i =>
            {
                int x = i % count, y = firstRow + i / count;
                var src = new SKRect((float)(x * srcW), (float)(y * srcH - offset), (float)((x + 1) * srcW), (float)((y + 1) * srcH - offset));
                using var bitmap = new SKBitmap(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawImage(stripImage, src, new SKRect(0, 0, TileSize, TileSize), sampling);
                }
                if (IsTransparent(bitmap))
                    return;
                Save(bitmap, layer.Id, nativeZoom, x, y, format, extension);
                tiles.Add($"{nativeZoom}/{x}/{y}");
            });
        }
        reducedCanvas.Flush();

        if (nativeZoom > 0)
            RenderImageLevels(layer, reduced, nativeZoom - 1, format, extension, tiles);
        log($"  {layer.Title}: {tiles.Count:N0} tiles");
        return new TileSet(layer.Id, nativeZoom, extension, tiles.ToArray());
    }

    /// <summary>Renders an image that covers exactly the world bounds (background map or fog of war).</summary>
    public TileSet RenderImage(LayerDef layer, SKBitmap image, int imageMaxZoom, string extension)
    {
        var tiles = new ConcurrentBag<string>();
        var format = extension == "jpg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
        RenderImageLevels(layer, image, imageMaxZoom, format, extension, tiles);
        log($"  {layer.Title}: {tiles.Count:N0} tiles");
        return new TileSet(layer.Id, imageMaxZoom, extension, tiles.ToArray());
    }

    private void RenderImageLevels(LayerDef layer, SKBitmap image, int imageMaxZoom, SKEncodedImageFormat format, string extension, ConcurrentBag<string> tiles)
    {
        using var image2 = SKImage.FromBitmap(image);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);

        for (int z = 0; z <= imageMaxZoom; z++)
        {
            int count = 1 << z;
            double srcW = (double)image.Width / count, srcH = (double)image.Height / count;
            int zoom = z;
            Parallel.For(0, count * count, i =>
            {
                int x = i % count, y = i / count;
                var src = new SKRect((float)(x * srcW), (float)(y * srcH), (float)((x + 1) * srcW), (float)((y + 1) * srcH));
                using var bitmap = new SKBitmap(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawImage(image2, src, new SKRect(0, 0, TileSize, TileSize), sampling);
                }
                if (IsTransparent(bitmap))
                    return;
                Save(bitmap, layer.Id, zoom, x, y, format, extension);
                tiles.Add($"{zoom}/{x}/{y}");
            });
        }
    }

    private static bool IsTransparent(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0)
                return false;
        return true;
    }

    private void Save(SKBitmap bitmap, string layerId, int z, int x, int y, SKEncodedImageFormat format, string extension)
    {
        string dir = Path.Combine(outputDirectory, "tiles", layerId, z.ToString(), x.ToString());
        Directory.CreateDirectory(dir);
        using var data = bitmap.Encode(format, format == SKEncodedImageFormat.Jpeg ? 85 : 100);
        using var file = File.Create(Path.Combine(dir, $"{y}.{extension}"));
        data.SaveTo(file);
    }

    /// <summary>Per-thread de-duplication marks: a feature is taken once per query.</summary>
    private sealed class QueryState(int featureCount)
    {
        public readonly int[] Stamp = new int[featureCount];
        public int QueryId;
    }

    /// <summary>Buckets features by 100 m cells for fast per-tile lookup.</summary>
    private sealed class SpatialGrid
    {
        private readonly int _cellsPerSide = (int)Math.Ceiling(WorldBounds.Size / CellSize);
        private readonly Dictionary<int, List<int>> _cells = [];

        public SpatialGrid(IReadOnlyList<MapFeature> features)
        {
            for (int i = 0; i < features.Count; i++)
            {
                var (minX, minY, maxX, maxY) = Bounds(features[i]);
                int cx0 = Cell(minX - WorldBounds.MinX), cx1 = Cell(maxX - WorldBounds.MinX);
                int cy0 = Cell(minY - WorldBounds.MinY), cy1 = Cell(maxY - WorldBounds.MinY);
                for (int cy = cy0; cy <= cy1; cy++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        int key = cy * _cellsPerSide + cx;
                        if (!_cells.TryGetValue(key, out var list))
                            _cells[key] = list = [];
                        list.Add(i);
                    }
            }
        }

        private int Cell(double offset) => Math.Clamp((int)(offset / CellSize), 0, _cellsPerSide - 1);

        private static (double, double, double, double) Bounds(MapFeature f)
        {
            switch (f.Shape)
            {
                case Shape.Line when f.Path is { Length: >= 2 } path:
                {
                    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                    for (int i = 0; i + 1 < path.Length; i += 2)
                    {
                        minX = Math.Min(minX, path[i]);
                        maxX = Math.Max(maxX, path[i]);
                        minY = Math.Min(minY, path[i + 1]);
                        maxY = Math.Max(maxY, path[i + 1]);
                    }
                    double h = f.Thickness / 2;
                    return (minX - h, minY - h, maxX + h, maxY + h);
                }
                case Shape.Box:
                {
                    double r = Math.Sqrt(f.SizeX * f.SizeX + f.SizeY * f.SizeY) / 2;
                    return (f.Position.X - r, f.Position.Y - r, f.Position.X + r, f.Position.Y + r);
                }
                default:
                {
                    double r = Math.Max(f.Thickness / 2, 1);
                    return (f.Position.X - r, f.Position.Y - r, f.Position.X + r, f.Position.Y + r);
                }
            }
        }

        public List<(int X, int Y)> CandidateTiles(int z, double tileWorld, double margin)
        {
            int count = 1 << z;
            var result = new HashSet<(int, int)>();
            foreach (int key in _cells.Keys)
            {
                int cx = key % _cellsPerSide, cy = key / _cellsPerSide;
                double x0 = cx * CellSize - margin, x1 = (cx + 1) * CellSize + margin;
                double y0 = cy * CellSize - margin, y1 = (cy + 1) * CellSize + margin;
                int tx0 = Math.Max(0, (int)Math.Floor(x0 / tileWorld)), tx1 = Math.Min(count - 1, (int)Math.Floor(x1 / tileWorld));
                int ty0 = Math.Max(0, (int)Math.Floor(y0 / tileWorld)), ty1 = Math.Min(count - 1, (int)Math.Floor(y1 / tileWorld));
                for (int ty = ty0; ty <= ty1; ty++)
                    for (int tx = tx0; tx <= tx1; tx++)
                        result.Add((tx, ty));
            }
            return [.. result];
        }

        public List<int> Query(double minX, double minY, double maxX, double maxY, QueryState state)
        {
            var hits = new List<int>();
            int queryId = ++state.QueryId;
            var stamp = state.Stamp;
            int cx0 = Cell(minX - WorldBounds.MinX), cx1 = Cell(maxX - WorldBounds.MinX);
            int cy0 = Cell(minY - WorldBounds.MinY), cy1 = Cell(maxY - WorldBounds.MinY);
            for (int cy = cy0; cy <= cy1; cy++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    if (!_cells.TryGetValue(cy * _cellsPerSide + cx, out var list))
                        continue;
                    foreach (int i in list)
                    {
                        if (stamp[i] == queryId)
                            continue;
                        stamp[i] = queryId;
                        hits.Add(i);
                    }
                }
            return hits;
        }
    }

    /// <summary>Draws features onto one tile. Paints are cached per tile (SKPaint is not thread safe).</summary>
    private sealed class FeaturePainter(SKCanvas canvas, double originX, double originY, double unitsPerPixel) : IDisposable
    {
        private readonly Dictionary<string, (SKPaint Fill, SKPaint Stroke, SKPaint Line)> _paints = [];

        private (SKPaint Fill, SKPaint Stroke, SKPaint Line) Paints(string color)
        {
            if (_paints.TryGetValue(color, out var p))
                return p;
            var c = SKColor.Parse(color);
            c.ToHsl(out float h, out float s, out float l);
            p = (
                new SKPaint { Color = c, IsAntialias = true, Style = SKPaintStyle.Fill },
                new SKPaint { Color = SKColor.FromHsl(h, s, Math.Max(0, l - 22)), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 },
                new SKPaint { Color = c, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round });
            _paints[color] = p;
            return p;
        }

        private float X(double worldX) => (float)((worldX - originX) / unitsPerPixel);
        private float Y(double worldY) => (float)((worldY - originY) / unitsPerPixel);

        public void Draw(MapFeature f)
        {
            var paints = Paints(f.Color);
            switch (f.Shape)
            {
                case Shape.Box:
                {
                    float w = (float)Math.Max(f.SizeX / unitsPerPixel, 1.2);
                    float h = (float)Math.Max(f.SizeY / unitsPerPixel, 1.2);
                    canvas.Save();
                    canvas.Translate(X(f.Position.X), Y(f.Position.Y));
                    canvas.RotateRadians((float)f.Yaw);
                    var rect = new SKRect(-w / 2, -h / 2, w / 2, h / 2);
                    canvas.DrawRect(rect, paints.Fill);
                    if (w >= 6 && h >= 6)
                        canvas.DrawRect(rect, paints.Stroke);
                    canvas.Restore();
                    break;
                }
                case Shape.Line when f.Path is { Length: >= 4 } path:
                {
                    using var skPath = new SKPath();
                    skPath.MoveTo(X(path[0]), Y(path[1]));
                    for (int i = 2; i + 1 < path.Length; i += 2)
                        skPath.LineTo(X(path[i]), Y(path[i + 1]));
                    paints.Line.StrokeWidth = (float)Math.Max(f.Thickness / unitsPerPixel, 1.0);
                    canvas.DrawPath(skPath, paints.Line);
                    break;
                }
                case Shape.Point:
                {
                    float r = (float)Math.Max(f.Thickness / 2 / unitsPerPixel, 0.9);
                    canvas.DrawCircle(X(f.Position.X), Y(f.Position.Y), r, paints.Fill);
                    break;
                }
            }
        }

        private const float MinIconPixels = 14, MaxIconPixels = 256;
        private static readonly SKSamplingOptions IconSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

        /// <summary>The game icon, upright and centred on the box, once the footprint is large enough to show it.</summary>
        public void DrawIcon(MapFeature f, SKImage icon)
        {
            float size = (float)(Math.Min(f.SizeX, f.SizeY) / unitsPerPixel) * 0.85f;
            if (size < MinIconPixels)
                return;
            size = Math.Min(size, MaxIconPixels);
            float x = X(f.Position.X), y = Y(f.Position.Y);
            canvas.DrawImage(icon, new SKRect(x - size / 2, y - size / 2, x + size / 2, y + size / 2), IconSampling);
        }

        public void Dispose()
        {
            foreach (var (fill, stroke, line) in _paints.Values)
            {
                fill.Dispose();
                stroke.Dispose();
                line.Dispose();
            }
        }
    }
}
