using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SatisfactoryMapRenderer.Serve;

/// <summary>
/// Copies a save from another machine with the system's ssh client. Authentication is left to ssh
/// (keys, agent, ~/.ssh/config); passwords are never asked for or stored.
/// </summary>
public static partial class SshDownloader
{
    public static async Task DownloadAsync(SaveSource source, string targetFile, Action<string> log, Action<string, int> progress, CancellationToken cancel)
    {
        string target = source.SshTarget.Trim(), path = source.SshPath.Trim();
        if (!TargetPattern().IsMatch(target))
            throw new SaveSourceException("Enter the SSH target as user@host (or a host from your ssh config).");
        if (path.Length == 0 || path.Contains('\n') || path.Contains('\0'))
            throw new SaveSourceException("Enter the path of a .sav file or of the folder with the saves on the remote machine.");
        if (source.SshPort is < 1 or > 65535)
            throw new SaveSourceException("The SSH port must be between 1 and 65535.");

        var start = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (string arg in new[] { "-p", source.SshPort.ToString(), "-o", "BatchMode=yes", "-o", "ConnectTimeout=15",
                     // A host seen for the first time is remembered without asking (there is nobody to ask); a changed key is refused.
                     "-o", "StrictHostKeyChecking=accept-new", "-T", "--", target, RemoteCommand(path) })
            start.ArgumentList.Add(arg);

        log($"Copying the save from {target}:{path}");
        progress("Copying the save game over SSH", 12);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new SaveSourceException("Could not start ssh.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new SaveSourceException("The ssh program was not found on this machine.");
        }
        using (process)
        {
            process.StandardInput.Close();
            var errors = process.StandardError.ReadToEndAsync(cancel);
            await using (var file = File.Create(targetFile))
                await process.StandardOutput.BaseStream.CopyToAsync(file, cancel);
            await process.WaitForExitAsync(cancel);
            string error = (await errors).Trim();
            if (process.ExitCode != 0 || new FileInfo(targetFile).Length == 0)
                throw new SaveSourceException(error.Length > 0 ? $"ssh: {error.Split('\n').Last().Trim()}" : $"ssh ended with exit code {process.ExitCode} and no data.");
        }
    }

    /// <summary>A .sav path is sent as is; for a folder the newest .sav below it is taken.</summary>
    private static string RemoteCommand(string path)
    {
        string quoted = path.StartsWith("~/", StringComparison.Ordinal) ? "~/" + Quote(path[2..]) : Quote(path);
        if (path.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))
            return $"cat -- {quoted}";
        return $"f=$(find {quoted} -type f -name '*.sav' -printf '%T@ %p\\n' 2>/dev/null | sort -rn | head -n 1 | cut -d' ' -f2-); " +
               "if [ -z \"$f\" ]; then echo 'no .sav file found in that folder' >&2; exit 3; fi; cat -- \"$f\"";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>No leading dash, so the target can never be read as an ssh option.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.@\-]*$")]
    private static partial Regex TargetPattern();
}
