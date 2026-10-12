using System.Diagnostics;
using SatisfactoryMapRenderer.Game;
using SatisfactoryMapRenderer.Rendering;
using SatisfactoryMapRenderer.Save;
using SatisfactoryMapRenderer.Serve;
using SatisfactoryMapRenderer.World;

namespace SatisfactoryMapRenderer;

internal static class Program
{
    private const string Usage = """
        Satisfactory Map Renderer - turns a .sav file into an interactive HTML map.

        Usage:
          SatisfactoryMapRenderer [save.sav] [options]

        Options:
          -o, --output <dir>        Output directory (default: <save name>_map next to the save)
          -z, --max-zoom <3-9>      Highest rendered zoom level (default: 9, about 6 cm per pixel)
          -b, --background <image>  Use this image as the map instead of the game's own map
              --maps <dir>          Folder with game-map.jpg, ground-map.jpg and relief-map.jpg. They replace the
                                    game's own map, ground map and relief map (default: maps in the current
                                    directory)
              --render-ground-map <file>
                                    Only draw the ground map (the world from above without any factory) into
                                    this .jpg, or .ppm for sizes above 16384, and stop
              --ground-map-size <px>  Edge length of that image (default: 16384)
              --dump-json [file]    Also write every decoded object with all properties as JSON
          -h, --help                Show this help

        Game assets (maps, icons, resource purities from your own installation):
          -g, --game <dir>          Satisfactory installation (default: found via Epic/Steam)
              --no-game             Do not read the game installation
              --oodle <dll>         Oodle library to use instead of downloading it once
              --cache <dir>         Cache for extracted game data (default: %LOCALAPPDATA%\SatisfactoryMapRenderer)
              --refresh-game-data   Extract the game data again even if it is cached
              --game-data <dir>     Exported game data, used instead of the installation unless --game is given
                                    (default: gamedata in the current directory)
              --export-game-data    Copy the level data and all icons of the game into the --game-data folder,
                                    so that later runs do not need the game; no save file is needed for this

        Server mode (choose the save in the browser: browse, drag and drop, dedicated server API or SSH):
              --serve [port]        Serve the map at http://localhost:<port>/ (default 8000) and render every save
                                    chosen there; a save file on the command line is optional
              --bind <address>      Address to listen on (default: localhost). Use * to be reachable from other
                                    machines; this requires an admin password
              --admin-password <pw> Only admins who log in with this password see the save game source panel.
                                    Better: set the environment variable SMR_ADMIN_PASSWORD instead

        Without a save path, the only .sav file in the current directory is used.
        Open <output>/index.html in a browser afterwards; no web server is needed.
        """;

    private sealed record Options
    {
        public string SavePath { get; set; } = "";
        public string? Output { get; set; }
        public int MaxZoom { get; set; } = 9;
        public string? Background { get; set; }
        public string? Relief { get; set; }
        public string? Ground { get; set; }
        public string? RenderGroundMap { get; set; }
        public int GroundMapSize { get; set; } = GroundMapRenderer.MaxJpegSize;
        public string MapsDirectory { get; set; } = "maps";
        public bool DumpJson { get; set; }
        public string? DumpJsonPath { get; set; }
        public bool UseGame { get; set; } = true;
        public string? GameDirectory { get; set; }
        public string? OodleLibrary { get; set; }
        public string CacheDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SatisfactoryMapRenderer");
        public bool RefreshGameData { get; set; }
        public string GameDataDirectory { get; set; } = "gamedata";
        public bool ExportGameData { get; set; }
        public int? ServePort { get; set; }
        public string Bind { get; set; } = MapServer.Localhost;
        public string? AdminPassword { get; set; } = Environment.GetEnvironmentVariable("SMR_ADMIN_PASSWORD") is { Length: > 0 } password ? password : null;
        public bool Help { get; set; }
    }

    private static int Main(string[] args)
    {
        // Session names and item names can contain non-ASCII characters.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Options options;
        try
        {
            options = Parse(args);
            if (options.Help)
                return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            Run(options);
            return 0;
        }
        catch (SaveFormatException ex)
        {
            Console.Error.WriteLine($"Could not read the save file: {ex.Message}");
            return 1;
        }
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        string? savePath = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {arg}.");
            switch (arg)
            {
                case "-h" or "--help" or "/?":
                    Console.WriteLine(Usage);
                    o.Help = true;
                    return o;
                case "-o" or "--output":
                    o.Output = Next();
                    break;
                case "-z" or "--max-zoom":
                    o.MaxZoom = int.TryParse(Next(), out int z) && z is >= 3 and <= 9 ? z : throw new ArgumentException("--max-zoom must be between 3 and 9.");
                    break;
                case "-b" or "--background":
                    o.Background = Next();
                    break;
                case "--maps":
                    o.MapsDirectory = Next();
                    break;
                case "--dump-json":
                    o.DumpJson = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        o.DumpJsonPath = args[++i];
                    break;
                case "-g" or "--game":
                    o.GameDirectory = Next();
                    break;
                case "--no-game":
                    o.UseGame = false;
                    break;
                case "--oodle":
                    o.OodleLibrary = Next();
                    break;
                case "--cache":
                    o.CacheDirectory = Next();
                    break;
                case "--serve":
                    o.ServePort = 8000;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int port))
                    {
                        o.ServePort = port is >= 1 and <= 65535 ? port : throw new ArgumentException("--serve needs a port between 1 and 65535.");
                        i++;
                    }
                    break;
                case "--bind":
                    o.Bind = Next();
                    break;
                case "--admin-password":
                    o.AdminPassword = Next();
                    break;
                case "--game-data":
                    o.GameDataDirectory = Next();
                    break;
                case "--render-ground-map":
                    o.RenderGroundMap = Path.GetFullPath(Next());
                    break;
                case "--ground-map-size":
                    o.GroundMapSize = int.TryParse(Next(), out int groundSize) && groundSize is >= 256 and <= 65536
                        ? groundSize : throw new ArgumentException("--ground-map-size must be between 256 and 65536.");
                    break;
                case "--export-game-data":
                    o.ExportGameData = true;
                    break;
                case "--refresh-game-data":
                    o.RefreshGameData = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw new ArgumentException($"Unknown option {arg}.");
                    savePath = savePath is null ? arg : throw new ArgumentException("Only one save file can be given.");
                    break;
            }
        }

        if (o.RenderGroundMap is not null && savePath is null)
        {
            o.SavePath = ExportOnly;
            return o;
        }
        if (o.ExportGameData && savePath is null)
        {
            o.SavePath = ExportOnly;
            return o;
        }
        if (o.ServePort is not null && o.AdminPassword is null && !MapServer.IsLoopback(o.Bind))
            throw new ArgumentException("Listening on other addresses than localhost needs an admin password (--admin-password or SMR_ADMIN_PASSWORD); " +
                                        "otherwise every visitor could replace the save game.");
        if (o.AdminPassword is { Length: < 8 })
            throw new ArgumentException("The admin password must have at least 8 characters.");
        if (o.ServePort is null)
            savePath ??= FindSaveInCurrentDirectory();
        if (savePath is not null && !File.Exists(savePath))
            throw new ArgumentException($"Save file not found: {savePath}");
        if (o.Background is not null && !File.Exists(o.Background))
            throw new ArgumentException($"Background image not found: {o.Background}");
        if (o.OodleLibrary is not null && !File.Exists(o.OodleLibrary))
            throw new ArgumentException($"Oodle library not found: {o.OodleLibrary}");

        // Map images kept in the maps folder take the place of the game's own maps.
        o.Background ??= ExistingFile(Path.Combine(o.MapsDirectory, GameMapFile));
        o.Relief = ExistingFile(Path.Combine(o.MapsDirectory, ReliefMapFile));
        o.Ground = ExistingFile(Path.Combine(o.MapsDirectory, GroundMapFile));

        if (savePath is null)
        {
            // Server mode without a save: the map is chosen in the browser.
            o.Output = Path.GetFullPath(o.Output ?? "map");
            return o;
        }
        o.SavePath = Path.GetFullPath(savePath);
        o.Output = Path.GetFullPath(o.Output ?? Path.Combine(Path.GetDirectoryName(o.SavePath)!, Path.GetFileNameWithoutExtension(o.SavePath) + "_map"));
        return o;
    }

    /// <summary>Stands in for the save path when only --export-game-data was asked for.</summary>
    private const string ExportOnly = "<export>";
    private const string GameMapFile = "game-map.jpg";
    private const string ReliefMapFile = "relief-map.jpg";
    private const string GroundMapFile = "ground-map.jpg";

    private static string? ExistingFile(string path) => File.Exists(path) ? Path.GetFullPath(path) : null;

    private static void Run(Options o)
    {
        var total = Stopwatch.StartNew();
        if (o.ExportGameData)
        {
            // Always read the installation here, never an earlier export.
            using var source = LoadGameAssets(o with { GameDirectory = o.GameDirectory ?? GameInstallation.Find(null)?.Directory ?? "" });
            if (source is null)
            {
                Console.Error.WriteLine("--export-game-data needs the game installation (use --game <dir>).");
                Environment.ExitCode = 1;
                return;
            }
            source.ExportTo(o.GameDataDirectory);
            if (o.SavePath == ExportOnly)
                return;
        }
        using var game = LoadGameAssets(o);
        if (o.RenderGroundMap is not null)
        {
            if (game is null || !GroundMapRenderer.Render(game, o.RenderGroundMap, o.GroundMapSize, Console.WriteLine))
                Environment.ExitCode = 1;
            if (o.SavePath == ExportOnly)
                return;
        }
        if (o.ServePort is { } port)
        {
            Serve(o, game, port);
            return;
        }
        Render(o, o.SavePath, game);
        Console.WriteLine($"Done in {total.Elapsed.TotalSeconds:0.0}s. Open {Path.Combine(o.Output!, "index.html")}");
    }

    /// <summary>Serves the output directory and renders whatever save the viewer asks for.</summary>
    private static void Serve(Options o, GameAssets? game, int port)
    {
        var server = new MapServer(o.Output!, o.CacheDirectory, port, (savePath, progress) =>
        {
            var sw = Stopwatch.StartNew();
            Render(o, savePath, game, progress);
            Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:0.0}s.");
        }, Console.WriteLine, o.Bind, o.AdminPassword);

        if (o.SavePath.Length > 0)
        {
            Render(o, o.SavePath, game);
            server.SetFileSource(o.SavePath);
        }
        else if (!File.Exists(Path.Combine(o.Output!, "map-data.js")))
            HtmlExporter.ExportEmpty(o.Output!, o.MaxZoom, game);
        else
        {
            HtmlExporter.RefreshViewer(o.Output!, game);
            RenderAgainAfterUpdate(o, game);
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        try
        {
            server.RunAsync(stop.Token).GetAwaiter().GetResult();
        }
        catch (System.Net.HttpListenerException ex)
        {
            Console.Error.WriteLine($"Could not listen on port {port}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>Names the version that rendered a map, so that a newer version knows the map is not its own.</summary>
    private const string RenderedStamp = ".rendered";

    /// <summary>
    /// A map on disk that another version rendered lacks what this version adds (base maps, the 3D view).
    /// The save it was made from is usually still in the cache: render it again.
    /// </summary>
    private static void RenderAgainAfterUpdate(Options o, GameAssets? game)
    {
        string stamp = Path.Combine(o.Output!, RenderedStamp);
        if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == HtmlExporter.Version)
            return;
        var name = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(o.Output!, "map-data.js")), "\"fileName\":\"([^\"]*)\"");
        if (!name.Success || name.Groups[1].Value.Length == 0)
            return;
        string save = Path.Combine(o.CacheDirectory, "saves", Path.GetFileName(System.Text.RegularExpressions.Regex.Unescape(name.Groups[1].Value)));
        if (!File.Exists(save))
        {
            Console.WriteLine("The map on disk was rendered by another version; load or refresh the save game to get everything this version shows.");
            return;
        }
        Console.WriteLine($"The map on disk was rendered by another version; rendering it again from {save}");
        try
        {
            Render(o, save, game);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Could not render the map again ({ex.Message}); load or refresh the save game in the page.");
        }
    }

    /// <param name="progress">Told what is being done and how far along the whole render is, in percent.</param>
    private static void Render(Options o, string savePath, GameAssets? game, Action<string, int>? progress = null)
    {
        progress?.Invoke("Reading the save game", 2);
        Console.WriteLine($"Loading {savePath}");
        var save = SaveGameReader.Load(savePath, message => Console.WriteLine("  " + message));

        var h = save.Header;
        Console.WriteLine($"  Session \"{h.SessionName}\", played {(int)h.PlayTime.TotalHours}h {h.PlayTime.Minutes:00}m");
        if (save.PropertiesFailed > 0)
            Console.WriteLine($"  {save.PropertiesFailed:N0} properties could not be decoded and were kept as raw bytes.");
        if (save.PropertyUnderflows.Count > 0)
            Console.WriteLine($"  {save.PropertyUnderflows.Values.Sum():N0} properties were only partly decoded, e.g. {save.PropertyUnderflows.Keys.First()}.");
        foreach (var warning in save.Warnings.Take(10))
            Console.WriteLine($"  Warning: {warning}");
        if (save.Warnings.Count > 10)
            Console.WriteLine($"  ... and {save.Warnings.Count - 10} more warnings");


        progress?.Invoke("Finding buildings and resources", 15);
        var world = WorldExtractor.Extract(save, game);
        Console.WriteLine($"Extracted {world.Features.Count:N0} map features");
        foreach (var layer in Layers.All)
        {
            int count = world.Features.Count(f => f.Layer == layer);
            if (count > 0)
                Console.WriteLine($"  {layer.Title,-30} {count,9:N0}");
        }

        if (o.Background is not null)
            Console.WriteLine($"Game map from {o.Background}");
        if (o.Relief is not null)
            Console.WriteLine($"Relief map from {o.Relief}");
        if (o.Ground is not null)
            Console.WriteLine($"Ground map from {o.Ground}");
        Console.WriteLine($"Rendering to {o.Output}");
        // The exporter reports each finished tile layer ("  Buildings: ... tiles"); those lines drive the bar.
        int layersDone = 0, layersTotal = Layers.All.Count(l => l.Kind == LayerKind.Raster);
        progress?.Invoke("Drawing the map", 25);
        MapExporter.Export(world, new ExportOptions(o.Output!, o.MaxZoom, o.Background, game, o.Relief, o.Ground), message =>
        {
            Console.WriteLine(message);
            if (message.StartsWith("  ", StringComparison.Ordinal) && message.Contains(':'))
                progress?.Invoke($"Drawing the map: {message.Trim().Split(':')[0].ToLowerInvariant()}", 25 + Math.Min(++layersDone, layersTotal) * 72 / layersTotal);
        });

        File.WriteAllText(Path.Combine(o.Output!, RenderedStamp), HtmlExporter.Version);

        if (o.DumpJson)
        {
            string jsonPath = o.DumpJsonPath ?? Path.Combine(o.Output!, "save.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
            SaveJsonExporter.Export(save, jsonPath);
            Console.WriteLine($"Wrote full save dump to {jsonPath}");
        }

    }

    /// <summary>Game assets are optional: without an installation the map falls back to the explored-area outline.</summary>
    private static GameAssets? LoadGameAssets(Options o)
    {
        if (!o.UseGame)
            return null;
        if (o.GameDirectory is null && !o.RefreshGameData && File.Exists(Path.Combine(o.GameDataDirectory, GameAssets.DataFileName)))
        {
            Console.WriteLine($"Game assets from {Path.GetFullPath(o.GameDataDirectory)}");
            if (GameAssets.LoadExported(o.GameDataDirectory, message => Console.WriteLine(message)) is { } exported)
                return exported;
            Console.WriteLine("  The exported game data is from another version of this tool; reading the installation instead.");
        }
        GameInstallation? installation;
        try
        {
            installation = GameInstallation.Find(o.GameDirectory);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"Game assets: {ex.Message}");
            return null;
        }
        if (installation is null)
        {
            Console.WriteLine("Game assets: no Satisfactory installation found (use --game <dir>); rendering without the game's maps and icons.");
            return null;
        }

        Console.WriteLine($"Game assets from {installation.Directory}");
        try
        {
            return GameAssets.Load(installation, o.CacheDirectory, o.OodleLibrary, o.RefreshGameData, message => Console.WriteLine(message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Game assets are an optional extra; any problem with them must not prevent the map.
            Console.WriteLine($"  Could not read the game assets ({ex.GetType().Name}: {ex.Message}); rendering without them.");
            return null;
        }
    }

    private static string FindSaveInCurrentDirectory()
    {
        var saves = Directory.GetFiles(Environment.CurrentDirectory, "*.sav");
        return saves.Length switch
        {
            1 => saves[0],
            0 => throw new ArgumentException("No save file given and none found in the current directory."),
            _ => throw new ArgumentException("Several .sav files found in the current directory; please choose one."),
        };
    }
}
