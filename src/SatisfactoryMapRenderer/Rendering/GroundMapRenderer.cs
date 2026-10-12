using System.Text.Json;
using SatisfactoryMapRenderer.Game;
using SatisfactoryMapRenderer.World;
using SkiaSharp;

namespace SatisfactoryMapRenderer.Rendering;

/// <summary>
/// The world seen from straight above, without anything a player built: the game's ground textures where
/// the landscape has them painted, water by its depth, cliff texture on steep faces, all of it lit by the
/// shape of the terrain. The same picture the 3D view draws on its terrain, computed here pixel by pixel
/// so that it can be as large as wanted.
/// </summary>
public sealed class GroundMapRenderer
{
    /// <summary>Larger than this, the picture no longer fits into one bitmap and is written as PPM instead.</summary>
    public const int MaxJpegSize = 16384;
    private const double WideTile = 7.3;
    private const int StripRows = 256, FallbackMapSize = 2048;
    private static readonly double SunLength = Math.Sqrt(0.55 * 0.55 + 0.62 * 0.62 + 0.55 * 0.55);
    private static readonly double SunX = -0.55 / SunLength, SunUp = 0.62 / SunLength, SunY = -0.55 / SunLength;

    /// <summary>A tiling texture, reduced so that one of its pixels is about one pixel of the map.</summary>
    private sealed record Tiling(int Size, float[] Rgb, double Repeat);

    private readonly GroundInfo _info;
    private readonly byte[] _ground, _map;
    private readonly int _groundWidth, _groundHeight, _mapSize, _heightSize, _cliff;
    private readonly float[] _slopeX, _slopeY;
    private readonly SKBitmap[] _textures;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (Tiling[] Near, Tiling[] Wide)> _tilings = new();

    /// <summary>Reads what the picture is made from; null if the game data lacks any of it.</summary>
    public static GroundMapRenderer? TryLoad(GameAssets game) =>
        game.Ground is { } info && game.TerrainHeightsPath is { } heights && game.MapImagePath is { } map ? new GroundMapRenderer(game, info, heights, map) : null;

    private GroundMapRenderer(GameAssets game, GroundInfo info, string heightsFile, string mapFile)
    {
        _info = info;
        double world = WorldBounds.Size;

        // which ground lies where
        using (var groundImage = SKBitmap.Decode(Path.Combine(game.GroundDirectory, GameGround.MapFile)).Copy(SKColorType.Rgba8888))
        {
            (_groundWidth, _groundHeight) = (groundImage.Width, groundImage.Height);
            _ground = groundImage.Bytes;
        }

        // heights, and from them the slope of the terrain
        var terrain = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(game.TerrainInfoPath));
        double zAt0 = terrain.GetProperty("zAt0").GetDouble(), zAt1 = terrain.GetProperty("zAt1").GetDouble();
        using (var heightImage = SKBitmap.Decode(heightsFile).Copy(SKColorType.Rgba8888))
        {
            int hs = _heightSize = heightImage.Width;
            float[] slopeX = _slopeX = new float[hs * hs], slopeY = _slopeY = new float[hs * hs];
            byte[] packed = heightImage.Bytes;
            float Height(int x, int y)
            {
                int i = (Math.Clamp(y, 0, hs - 1) * hs + Math.Clamp(x, 0, hs - 1)) * 4;
                return (float)(zAt0 + (packed[i] * 256 + packed[i + 1]) / 65535.0 * (zAt1 - zAt0));
            }
            double cell = world / (hs - 1);
            Parallel.For(0, hs, y =>
            {
                for (int x = 0; x < hs; x++)
                {
                    slopeX[y * hs + x] = (float)((Height(x + 1, y) - Height(x - 1, y)) / (2 * cell));
                    slopeY[y * hs + x] = (float)((Height(x, y + 1) - Height(x, y - 1)) / (2 * cell));
                }
            });
        }

        // the in-game map, for everything beyond the landscape; a small copy is enough for that
        using (var full = SKBitmap.Decode(mapFile))
        using (var small = full.Resize(new SKImageInfo(FallbackMapSize, FallbackMapSize, SKColorType.Rgba8888, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
        {
            _mapSize = FallbackMapSize;
            _map = small.Bytes;
        }

        _textures = [.. info.Layers.Select(layer => SKBitmap.Decode(Path.Combine(game.GroundDirectory, layer)))];
        _cliff = Array.IndexOf(info.Layers, "cliff.jpg");
    }

    /// <summary>The ground textures for a map of this many cm per pixel: fine, and in wide repetitions as in the 3D view.</summary>
    private (Tiling[] Near, Tiling[] Wide) Tilings(double pixel) => _tilings.GetOrAdd((long)Math.Round(pixel * 1000), _ =>
        ([.. _textures.Select(t => Reduce(t, _info.Tile, pixel))], [.. _textures.Select(t => Reduce(t, _info.Tile * WideTile, pixel))]));

    /// <summary>
    /// Draws a part of the world: <paramref name="rows"/> rows of <paramref name="width"/> pixels of
    /// <paramref name="pixel"/> cm, the first one at the world position given, as red, green, blue.
    /// </summary>
    public void RenderArea(double originX, double firstY, double pixel, int width, int rows, byte[] rgb, bool parallel = true)
    {
        var (near, wide) = Tilings(pixel);
        double world = WorldBounds.Size;
        void Row(int row)
        {
            Span<float> c = stackalloc float[3], t = stackalloc float[3];
            double wy = firstY + (row + 0.5) * pixel;
            for (int x = 0; x < width; x++)
            {
                double wx = originX + (x + 0.5) * pixel;

                // the in-game map here, and whether it shows sea
                double mx = (wx - WorldBounds.MinX) / world * _mapSize - 0.5, my = (wy - WorldBounds.MinY) / world * _mapSize - 0.5;
                int m0 = (Math.Clamp((int)Math.Round(my), 0, _mapSize - 1) * _mapSize + Math.Clamp((int)Math.Round(mx), 0, _mapSize - 1)) * 4;
                float fr = _map[m0] / 255f, fg = _map[m0 + 1] / 255f, fb = _map[m0 + 2] / 255f;
                float beyondDepth = fb > fr * 1.12f && fg > fr ? 3000f : 0f;

                // the four samples of the ground map around the point, blended
                double gx = (wx - _info.MinX) / _info.Cell, gy = (wy - _info.MinY) / _info.Cell;
                int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
                float tx = (float)(gx - ix), ty = (float)(gy - iy);
                float r = 0, g = 0, b = 0, depth = 0;
                for (int k = 0; k < 4; k++)
                {
                    int sx = ix + (k & 1), sy = iy + (k >> 1);
                    float weight = ((k & 1) == 0 ? 1 - tx : tx) * ((k >> 1) == 0 ? 1 - ty : ty);
                    if (weight <= 0)
                        continue;
                    int firstLayer = 15, secondLayer = 15, share = 255, steps = 0;
                    if (sx >= 0 && sy >= 0 && sx < _groundWidth && sy < _groundHeight)
                    {
                        int o = (sy * _groundWidth + sx) * 4;
                        (firstLayer, secondLayer, share, steps) = (_ground[o] >> 4, _ground[o] & 15, _ground[o + 1], _ground[o + 2]);
                    }
                    if (firstLayer >= _info.Layers.Length)
                    {
                        r += fr * weight; g += fg * weight; b += fb * weight;
                        depth += beyondDepth * weight;
                        continue;
                    }
                    Sample(near[firstLayer], wide[firstLayer], wx, wy, c);
                    if (secondLayer < _info.Layers.Length && share < 255)
                    {
                        Sample(near[secondLayer], wide[secondLayer], wx, wy, t);
                        float s = share / 255f;
                        for (int i = 0; i < 3; i++)
                            c[i] = t[i] + (c[i] - t[i]) * s;
                    }
                    r += c[0] * weight; g += c[1] * weight; b += c[2] * weight;
                    depth += (float)(steps * _info.DepthStep) * weight;
                }

                // the shape of the terrain: cliffs on steep faces, light and shade
                double px = (wx - WorldBounds.MinX) / world * (_heightSize - 1), py = (wy - WorldBounds.MinY) / world * (_heightSize - 1);
                float slX = Bilinear(_slopeX, _heightSize, px, py), slY = Bilinear(_slopeY, _heightSize, px, py);
                float length = MathF.Sqrt(slX * slX + slY * slY + 1), up = 1 / length;
                float steep = _cliff < 0 ? 0 : Smooth(0.80f, 0.60f, up);
                if (steep > 0)
                {
                    Sample(near[_cliff], wide[_cliff], wx, wy, c);
                    r += (c[0] * 1.3f - r) * steep; g += (c[1] * 1.3f - g) * steep; b += (c[2] * 1.3f - b) * steep;
                }
                depth *= Smooth(0.75f, 0.93f, up);
                if (depth > 0)
                {
                    float deep = Smooth(0, 1500, depth), cover = Math.Clamp(0.30f + depth / 500f, 0, 0.93f);
                    r += (0.16f + (0.04f - 0.16f) * deep - r) * cover;
                    g += (0.40f + (0.17f - 0.40f) * deep - g) * cover;
                    b += (0.44f + (0.27f - 0.44f) * deep - b) * cover;
                }
                float light = (float)(0.42 + 0.78 * Math.Max((-slX * SunX + SunUp - slY * SunY) / length, 0) + 0.12 * up);

                int at = (row * width + x) * 3;
                rgb[at] = (byte)Math.Clamp(r * light * 255f + 0.5f, 0, 255);
                rgb[at + 1] = (byte)Math.Clamp(g * light * 255f + 0.5f, 0, 255);
                rgb[at + 2] = (byte)Math.Clamp(b * light * 255f + 0.5f, 0, 255);
            }
        }
        if (parallel)
            Parallel.For(0, rows, Row);
        else
            for (int row = 0; row < rows; row++)
                Row(row);
    }

    /// <summary>The whole world as one picture of <paramref name="size"/> × <paramref name="size"/> px.</summary>
    public static bool Render(GameAssets game, string file, int size, Action<string> log)
    {
        if (TryLoad(game) is not { } source)
        {
            log("  Ground map: the game data has no ground textures, heights or map to make it from");
            return false;
        }
        bool ppm = Path.GetExtension(file).Equals(".ppm", StringComparison.OrdinalIgnoreCase);
        if (!ppm && size > MaxJpegSize)
            throw new ArgumentException($"A ground map of more than {MaxJpegSize} px can only be written as .ppm.");
        double pixel = WorldBounds.Size / size;

        using var output = ppm ? File.Create(file) : null;
        output?.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{size} {size}\n255\n"));
        using var bitmap = ppm ? null : new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque));

        byte[] strip = new byte[size * StripRows * 3];
        for (int top = 0; top < size; top += StripRows)
        {
            int rows = Math.Min(StripRows, size - top);
            source.RenderArea(WorldBounds.MinX, WorldBounds.MinY + top * pixel, pixel, size, rows, strip);
            if (output is not null)
            {
                output.Write(strip, 0, rows * size * 3);
            }
            else
            {
                var pixels = bitmap!.GetPixelSpan();
                for (int i = 0, o = top * size * 4; i < rows * size; i++, o += 4)
                {
                    pixels[o] = strip[i * 3];
                    pixels[o + 1] = strip[i * 3 + 1];
                    pixels[o + 2] = strip[i * 3 + 2];
                    pixels[o + 3] = 255;
                }
            }
        }

        if (bitmap is not null)
        {
            using var stream = File.Create(file);
            if (!bitmap.Encode(stream, SKEncodedImageFormat.Jpeg, 90))
                throw new InvalidOperationException("the ground map could not be written as JPEG");
        }
        output?.Dispose();
        log($"  Ground map: {size} px, {pixel:0.#} cm per pixel, {new FileInfo(file).Length / 1048576.0:0.0} MB");
        return true;
    }

    /// <summary>The texture at the size at which one repetition covers <paramref name="tile"/> cm of the map.</summary>
    private static Tiling Reduce(SKBitmap texture, double tile, double pixel)
    {
        int size = (int)Math.Clamp(Math.Round(tile / pixel), 1, texture.Width);
        using var small = size == texture.Width ? null : texture.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var rgba = (small ?? texture).Copy(SKColorType.Rgba8888);
        byte[] bytes = rgba.Bytes;
        float[] rgb = new float[size * size * 3];
        for (int i = 0; i < size * size; i++)
            for (int k = 0; k < 3; k++)
                rgb[i * 3 + k] = bytes[i * 4 + k] / 255f;
        return new Tiling(size, rgb, tile);
    }

    /// <summary>The ground texture at a place: half the fine pattern, half the wide one, which hides the repetition.</summary>
    private static void Sample(Tiling near, Tiling wide, double x, double y, Span<float> color)
    {
        Span<float> other = stackalloc float[3];
        Tile(near, x, y, color);
        Tile(wide, y, x, other);
        for (int i = 0; i < 3; i++)
            color[i] = (color[i] + other[i]) * 0.5f;
    }

    private static void Tile(Tiling t, double x, double y, Span<float> color)
    {
        double u = x / t.Repeat * t.Size - 0.5, v = y / t.Repeat * t.Size - 0.5;
        double fu = Math.Floor(u), fv = Math.Floor(v);
        float a = (float)(u - fu), b = (float)(v - fv);
        int n = t.Size, x0 = (int)(((long)fu % n + n) % n), y0 = (int)(((long)fv % n + n) % n), x1 = (x0 + 1) % n, y1 = (y0 + 1) % n;
        int i00 = (y0 * n + x0) * 3, i10 = (y0 * n + x1) * 3, i01 = (y1 * n + x0) * 3, i11 = (y1 * n + x1) * 3;
        for (int k = 0; k < 3; k++)
            color[k] = (t.Rgb[i00 + k] * (1 - a) + t.Rgb[i10 + k] * a) * (1 - b) + (t.Rgb[i01 + k] * (1 - a) + t.Rgb[i11 + k] * a) * b;
    }

    private static float Bilinear(float[] values, int size, double x, double y)
    {
        x = Math.Clamp(x, 0, size - 1);
        y = Math.Clamp(y, 0, size - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, size - 1), y1 = Math.Min(y0 + 1, size - 1);
        float a = (float)(x - x0), b = (float)(y - y0);
        return (values[y0 * size + x0] * (1 - a) + values[y0 * size + x1] * a) * (1 - b) + (values[y1 * size + x0] * (1 - a) + values[y1 * size + x1] * a) * b;
    }

    /// <summary>0 at <paramref name="from"/>, 1 at <paramref name="to"/>, smooth in between.</summary>
    private static float Smooth(float from, float to, float value)
    {
        float t = Math.Clamp((value - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
