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
public static class GroundMapRenderer
{
    /// <summary>Larger than this, the picture no longer fits into one bitmap and is written as PPM instead.</summary>
    public const int MaxJpegSize = 16384;
    private const double WideTile = 7.3;
    private const int StripRows = 256;

    /// <summary>A tiling texture, reduced so that one of its pixels is about one pixel of the map.</summary>
    private sealed record Tiling(int Size, float[] Rgb, double Repeat);

    public static bool Render(GameAssets game, string file, int size, Action<string> log)
    {
        if (game.Ground is not { } info || game.TerrainHeightsPath is not { } heightsFile || game.MapImagePath is not { } mapFile)
        {
            log("  Ground map: the game data has no ground textures, heights or map to make it from");
            return false;
        }
        bool ppm = Path.GetExtension(file).Equals(".ppm", StringComparison.OrdinalIgnoreCase);
        if (!ppm && size > MaxJpegSize)
            throw new ArgumentException($"A ground map of more than {MaxJpegSize} px can only be written as .ppm.");

        double world = WorldBounds.Size, pixel = world / size;

        // which ground lies where
        using var groundImage = SKBitmap.Decode(Path.Combine(game.GroundDirectory, GameGround.MapFile)).Copy(SKColorType.Rgba8888);
        int gw = groundImage.Width, gh = groundImage.Height;
        byte[] ground = groundImage.Bytes;

        // heights, and from them the slope of the terrain
        var terrain = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(game.TerrainInfoPath));
        double zAt0 = terrain.GetProperty("zAt0").GetDouble(), zAt1 = terrain.GetProperty("zAt1").GetDouble();
        using var heightImage = SKBitmap.Decode(heightsFile).Copy(SKColorType.Rgba8888);
        int hs = heightImage.Width;
        float[] slopeX = new float[hs * hs], slopeY = new float[hs * hs];
        {
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

        // the in-game map, for everything beyond the landscape
        using var mapImage = SKBitmap.Decode(mapFile).Copy(SKColorType.Rgba8888);
        int ms = mapImage.Width, mh = mapImage.Height;
        byte[] map = mapImage.Bytes;

        // the ground textures, fine and in wide repetitions as in the 3D view
        var near = new Tiling[info.Layers.Length];
        var wide = new Tiling[info.Layers.Length];
        for (int l = 0; l < info.Layers.Length; l++)
        {
            using var texture = SKBitmap.Decode(Path.Combine(game.GroundDirectory, info.Layers[l]));
            near[l] = Reduce(texture, info.Tile, pixel);
            wide[l] = Reduce(texture, info.Tile * WideTile, pixel);
        }
        int cliff = Array.IndexOf(info.Layers, "cliff.jpg");

        double sunLength = Math.Sqrt(0.55 * 0.55 + 0.62 * 0.62 + 0.55 * 0.55);
        double sunX = -0.55 / sunLength, sunUp = 0.62 / sunLength, sunY = -0.55 / sunLength;

        using var output = ppm ? File.Create(file) : null;
        output?.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{size} {size}\n255\n"));
        using var bitmap = ppm ? null : new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque));

        byte[] strip = new byte[size * StripRows * 3];
        for (int top = 0; top < size; top += StripRows)
        {
            int rows = Math.Min(StripRows, size - top), first = top;
            Parallel.For(0, rows, row =>
            {
                Span<float> c = stackalloc float[3], t = stackalloc float[3];
                double wy = WorldBounds.MinY + (first + row + 0.5) * pixel;
                for (int x = 0; x < size; x++)
                {
                    double wx = WorldBounds.MinX + (x + 0.5) * pixel;

                    // the in-game map here, and whether it shows sea
                    double mx = (x + 0.5) / size * ms - 0.5, my = (first + row + 0.5) / size * mh - 0.5;
                    int m0 = (Math.Clamp((int)Math.Round(my), 0, mh - 1) * ms + Math.Clamp((int)Math.Round(mx), 0, ms - 1)) * 4;
                    float fr = map[m0] / 255f, fg = map[m0 + 1] / 255f, fb = map[m0 + 2] / 255f;
                    float beyondDepth = fb > fr * 1.12f && fg > fr ? 3000f : 0f;

                    // the four samples of the ground map around the point, blended
                    double gx = (wx - info.MinX) / info.Cell, gy = (wy - info.MinY) / info.Cell;
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
                        if (sx >= 0 && sy >= 0 && sx < gw && sy < gh)
                        {
                            int o = (sy * gw + sx) * 4;
                            (firstLayer, secondLayer, share, steps) = (ground[o] >> 4, ground[o] & 15, ground[o + 1], ground[o + 2]);
                        }
                        if (firstLayer >= info.Layers.Length)
                        {
                            r += fr * weight; g += fg * weight; b += fb * weight;
                            depth += beyondDepth * weight;
                            continue;
                        }
                        Sample(near[firstLayer], wide[firstLayer], wx, wy, c);
                        if (secondLayer < info.Layers.Length && share < 255)
                        {
                            Sample(near[secondLayer], wide[secondLayer], wx, wy, t);
                            float s = share / 255f;
                            for (int i = 0; i < 3; i++)
                                c[i] = t[i] + (c[i] - t[i]) * s;
                        }
                        r += c[0] * weight; g += c[1] * weight; b += c[2] * weight;
                        depth += (float)(steps * info.DepthStep) * weight;
                    }

                    // the shape of the terrain: cliffs on steep faces, light and shade
                    double px = (wx - WorldBounds.MinX) / world * (hs - 1), py = (wy - WorldBounds.MinY) / world * (hs - 1);
                    float slX = Bilinear(slopeX, hs, px, py), slY = Bilinear(slopeY, hs, px, py);
                    float length = MathF.Sqrt(slX * slX + slY * slY + 1), up = 1 / length;
                    float steep = cliff < 0 ? 0 : Smooth(0.80f, 0.60f, up);
                    if (steep > 0)
                    {
                        Sample(near[cliff], wide[cliff], wx, wy, c);
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
                    float light = (float)(0.42 + 0.78 * Math.Max((-slX * sunX + sunUp - slY * sunY) / length, 0) + 0.12 * up);

                    int at = (row * size + x) * 3;
                    strip[at] = (byte)Math.Clamp(r * light * 255f + 0.5f, 0, 255);
                    strip[at + 1] = (byte)Math.Clamp(g * light * 255f + 0.5f, 0, 255);
                    strip[at + 2] = (byte)Math.Clamp(b * light * 255f + 0.5f, 0, 255);
                }
            });

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
