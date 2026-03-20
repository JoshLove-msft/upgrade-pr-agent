using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace UpgradePrAgent;

/// <summary>Checks GitHub releases for a newer version and self-updates.</summary>
public static class AutoUpdater
{
    private const string Repo = "JoshLove-msft/upgrade-pr-agent";

    public static async Task<bool> CheckAndUpdateAsync()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var currentStr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

            Log.Debug($"Current version: {currentStr}");

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("UpgradePrAgent");
            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/{Repo}/releases/latest");
            var release = JsonDocument.Parse(json).RootElement;
            var tag = release.GetProperty("tag_name").GetString()!.TrimStart('v');

            if (tag == currentStr)
            {
                Log.Debug("Already on latest version");
                return false;
            }

            Log.Info($"New version available: {tag} (current: {currentStr}). Updating...");

            // Download and reinstall via the install script
            var psi = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                $"irm https://raw.githubusercontent.com/{Repo}/master/install.ps1 | iex");

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                Log.Info($"Updated to {tag}. Restart the agent to use the new version.");
                return true;
            }
            else
            {
                Log.Warn($"Auto-update failed: {stderr.Trim()}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Update check failed: {ex.Message}");
            return false;
        }
    }
}
