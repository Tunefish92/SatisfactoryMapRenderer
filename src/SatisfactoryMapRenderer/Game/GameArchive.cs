using AssetRipper.TextureDecoder.Bc;
using AssetRipper.TextureDecoder.Rgb.Formats;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using SkiaSharp;

namespace SatisfactoryMapRenderer.Game;

/// <summary>Read access to the installed game's IoStore archives via CUE4Parse.</summary>
public sealed class GameArchive : IDisposable
{
    /// <summary>A small texture every game version has; used to verify the engine version guess.</summary>
    private const string ProbeTexture = "/Game/FactoryGame/Interface/UI/Assets/Map/MapCompass_Icon_player.MapCompass_Icon_player";

    private static readonly EGame[] EngineVersions =
        [EGame.GAME_UE5_6, EGame.GAME_UE5_5, EGame.GAME_UE5_7, EGame.GAME_UE5_4, EGame.GAME_UE5_3, EGame.GAME_UE5_2];

    private readonly DefaultFileProvider _provider;

    private GameArchive(DefaultFileProvider provider, EGame engine)
    {
        _provider = provider;
        Engine = engine;
    }

    public EGame Engine { get; }

    public IEnumerable<string> Files => _provider.Files.Keys;

    /// <param name="cacheDirectory">Holds the Oodle library and the corrected type mappings.</param>
    /// <param name="oodleLibrary">Use this Oodle DLL instead of the cached/downloaded one.</param>
    public static GameArchive Open(GameInstallation game, string cacheDirectory, string? oodleLibrary, Action<string> log)
    {
        Directory.CreateDirectory(cacheDirectory);
        InitializeOodle(cacheDirectory, oodleLibrary, log);

        var mappings = LoadMappings(game, cacheDirectory, log);
        foreach (var engine in EngineVersions)
        {
            var provider = new DefaultFileProvider(game.PaksDirectory, SearchOption.TopDirectoryOnly,
                new VersionContainer(engine), StringComparer.OrdinalIgnoreCase)
            {
                MappingsContainer = mappings,
            };
            provider.Initialize();
            provider.Mount();
            var archive = new GameArchive(provider, engine);
            if (archive.TryLoadTexture(ProbeTexture, 64) is { } probe)
            {
                probe.Dispose();
                return archive;
            }
            archive.Dispose();
        }
        throw new InvalidOperationException("The game's assets could not be read with any known Unreal Engine version.");
    }

    private static void InitializeOodle(string cacheDirectory, string? oodleLibrary, Action<string> log)
    {
        if (OodleHelper.Instance is not null)
            return;
        string path = oodleLibrary ?? Path.Combine(cacheDirectory, OodleHelper.OodleFileName);
        if (!File.Exists(path))
        {
            if (oodleLibrary is not null)
                throw new FileNotFoundException("Oodle library not found.", oodleLibrary);
            log("Downloading the Oodle decompression library (one-time, from github.com/WorkingRobot/OodleUE)...");
            string? downloaded = path;
            if (!OodleHelper.DownloadOodleDll(ref downloaded) || downloaded is null)
                throw new InvalidOperationException("Could not download the Oodle library. Pass one with --oodle <path>.");
            path = downloaded;
        }
        OodleHelper.Initialize(path);
    }

    private static FileUsmapTypeMappingsProvider LoadMappings(GameInstallation game, string cacheDirectory, Action<string> log)
    {
        try
        {
            return new FileUsmapTypeMappingsProvider(game.MappingsFile, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string fixedPath = Path.Combine(cacheDirectory, "FactoryGame.fixed.usmap");
            File.WriteAllBytes(fixedPath, UsmapFixer.Fix(File.ReadAllBytes(game.MappingsFile), out int count));
            log($"  Repaired the game's type mappings ({count} optional properties).");
            return new FileUsmapTypeMappingsProvider(fixedPath, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>"/Game/Path/Asset.Object" → "FactoryGame/Content/Path/Asset.Object".</summary>
    public static string ToProviderPath(string objectPath) =>
        objectPath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ? "FactoryGame/Content/" + objectPath["/Game/".Length..] : objectPath;

    /// <summary>"/Game/X/Desc_Foo.Desc_Foo_C" → the package file "FactoryGame/Content/X/Desc_Foo.uasset".</summary>
    public static string PackageFile(string objectPath)
    {
        string path = ToProviderPath(objectPath);
        int dot = path.LastIndexOf('.');
        return (dot > path.LastIndexOf('/') ? path[..dot] : path) + ".uasset";
    }

    public IEnumerable<UObject> LoadExports(string packageFile) => _provider.LoadPackage(packageFile).GetExports();

    public UObject? TryLoadObject(string objectPath)
    {
        try
        {
            return _provider.LoadPackageObject(ToProviderPath(objectPath));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Loads a texture and decodes the largest mip not exceeding <paramref name="maxSize"/> (0 = full size).</summary>
    public SKBitmap? TryLoadTexture(string objectPath, int maxSize)
    {
        try
        {
            return TryLoadObject(objectPath) is UTexture texture ? Decode(texture, maxSize) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>A square single-channel 16-bit float texture (PF_R16F) as floats, e.g. the world's height data.</summary>
    public (float[] Values, int Size)? TryLoadFloatTexture(string objectPath)
    {
        try
        {
            if (TryLoadObject(objectPath) is not UTexture { Format: EPixelFormat.PF_R16F } texture
                || texture.GetFirstMip() is not { BulkData.Data: { } data } mip || mip.SizeX != mip.SizeY || data.Length < mip.SizeX * mip.SizeY * 2)
                return null;
            float[] values = new float[mip.SizeX * mip.SizeY];
            var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(data);
            for (int i = 0; i < values.Length; i++)
                values[i] = (float)halves[i];
            return (values, mip.SizeX);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The layers of a texture array (or the one image of a plain texture), each at most <paramref name="maxSize"/> px.</summary>
    public static List<SKBitmap> DecodeLayers(UTexture texture, int maxSize)
    {
        var result = new List<SKBitmap>();
        var mip = (texture.PlatformData?.Mips ?? []).FirstOrDefault(m => m.BulkData?.Data is not null && m.SizeX <= maxSize && m.SizeY <= maxSize)
                  ?? texture.GetFirstMip();
        if (mip?.BulkData?.Data is not { } data)
            return result;
        int layers = Math.Max(mip.SizeZ, 1), bytes = data.Length / layers;
        for (int layer = 0; layer < layers; layer++)
            if (Decode(texture.Format, data.AsSpan(layer * bytes, bytes).ToArray(), mip.SizeX, mip.SizeY) is { } bitmap)
                result.Add(bitmap);
        return result;
    }

    public static SKBitmap? Decode(UTexture texture, int maxSize)
    {
        var mip = maxSize > 0 ? texture.GetMipByMaxSize(maxSize) ?? texture.GetFirstMip() : texture.GetFirstMip();
        byte[]? data = mip?.BulkData?.Data;
        if (mip is null || data is null)
            return null;
        return Decode(texture.Format, data, mip.SizeX, mip.SizeY);
    }

    private static SKBitmap? Decode(EPixelFormat format, byte[] data, int width, int height)
    {
        int blockWidth = (width + 3) / 4 * 4, blockHeight = (height + 3) / 4 * 4;
        byte[] bgra;
        switch (format)
        {
            case EPixelFormat.PF_DXT1: Bc1.Decompress<ColorBGRA<byte>, byte>(data, blockWidth, blockHeight, out bgra); break;
            case EPixelFormat.PF_DXT3: Bc2.Decompress<ColorBGRA<byte>, byte>(data, blockWidth, blockHeight, out bgra); break;
            case EPixelFormat.PF_DXT5: Bc3.Decompress<ColorBGRA<byte>, byte>(data, blockWidth, blockHeight, out bgra); break;
            case EPixelFormat.PF_BC4: Bc4.Decompress<ColorBGRA<byte>, byte>(data, blockWidth, blockHeight, out bgra); break;
            case EPixelFormat.PF_BC5: Bc5.Decompress<ColorBGRA<byte>, byte>(data, blockWidth, blockHeight, out bgra); break;
            case EPixelFormat.PF_BC7: Bc7.Decompress<ColorBGRA<byte>, byte>(data, blockWidth, blockHeight, out bgra); break;
            case EPixelFormat.PF_B8G8R8A8:
                bgra = data;
                blockWidth = width;
                break;
            case EPixelFormat.PF_R8G8B8A8:
                bgra = (byte[])data.Clone();
                for (int i = 0; i + 3 < bgra.Length; i += 4)
                    (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
                blockWidth = width;
                break;
            case EPixelFormat.PF_G8:
                bgra = new byte[width * height * 4];
                for (int i = 0; i < width * height; i++)
                {
                    bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = data[i];
                    bgra[i * 4 + 3] = 255;
                }
                blockWidth = width;
                break;
            default:
                return null;
        }

        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var pixels = bitmap.GetPixelSpan();
        for (int y = 0; y < height; y++)
            bgra.AsSpan(y * blockWidth * 4, width * 4).CopyTo(pixels.Slice(y * width * 4, width * 4));
        return bitmap;
    }

    public void Dispose() => _provider.Dispose();
}
