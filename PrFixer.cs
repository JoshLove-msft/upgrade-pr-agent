using System.Diagnostics;
using System.Text.RegularExpressions;

namespace UpgradePrAgent;

/// <summary>Clones the PR branch, applies fixes, commits and pushes.</summary>
public sealed partial class PrFixer(AgentConfig config)
{
    private string? _workDir;

    public async Task<bool> FixIssuesAsync(PrAnalysis analysis)
    {
        if (analysis.FixableIssues.Count == 0) return true;

        Log.Info($"Attempting to fix {analysis.FixableIssues.Count} issue(s) in PR #{analysis.Number}");

        if (config.DryRun)
        {
            foreach (var issue in analysis.FixableIssues)
                Log.Info($"[DRY RUN] Would fix: {issue.Description} ({issue.FixType})");
            return true;
        }

        try
        {
            await SetupWorkspaceAsync(analysis);
            var fixedCount = 0;

            foreach (var issue in analysis.FixableIssues)
            {
                if (await FixIssueAsync(issue)) fixedCount++;
                else Log.Warn($"Could not fix: {issue.Description}");
            }

            if (fixedCount > 0)
                await CommitAndPushAsync(analysis, fixedCount);

            return fixedCount == analysis.FixableIssues.Count;
        }
        catch (Exception ex)
        {
            Log.Error($"Error fixing issues in PR #{analysis.Number}: {ex.Message}");
            return false;
        }
        finally
        {
            Cleanup();
        }
    }

    private async Task SetupWorkspaceAsync(PrAnalysis analysis)
    {
        _workDir = config.WorkspaceDir is not null
            ? Path.Combine(config.WorkspaceDir, $"pr-{analysis.Number}")
            : Path.Combine(Path.GetTempPath(), $"upgrade-pr-{analysis.Number}-{Guid.NewGuid():N}");

        var repoUrl = $"https://github.com/{config.Owner}/{config.Repo}.git";
        Log.Info($"Cloning {repoUrl} (branch {analysis.HeadRef}) to {_workDir}");

        await RunGitAsync("clone", "--depth", "1",
            "--branch", analysis.HeadRef,
            "--single-branch", repoUrl, _workDir);
    }

    private async Task<bool> FixIssueAsync(FixableIssue issue) => issue.FixType switch
    {
        "lockfile_stale" => await FixLockfileAsync(issue),
        "version_mismatch" => await FixVersionMismatchAsync(issue),
        "regen_needed" => await FixRegenAsync(),
        _ => false,
    };

    private async Task<bool> FixLockfileAsync(FixableIssue issue)
    {
        var pkgDir = _workDir!;
        if (issue.FilePath.Contains("http-client-csharp"))
            pkgDir = Path.Combine(_workDir!, "eng", "packages", "http-client-csharp");
        else if (issue.FilePath.Contains("emitter-package"))
            pkgDir = Path.Combine(_workDir!, "eng");

        if (!File.Exists(Path.Combine(pkgDir, "package.json")))
        {
            Log.Error($"package.json not found at {pkgDir}");
            return false;
        }

        Log.Info($"Running npm install in {pkgDir}");
        var (exitCode, _, stderr) = await RunProcessAsync("npm",
            ["install", "--package-lock-only"], pkgDir);

        if (exitCode != 0) { Log.Error($"npm install failed: {stderr}"); return false; }

        Log.Info("Successfully regenerated lockfile");
        return true;
    }

    private async Task<bool> FixVersionMismatchAsync(FixableIssue issue)
    {
        var filePath = Path.Combine(_workDir!, issue.FilePath);
        if (!File.Exists(filePath)) { Log.Error($"File not found: {filePath}"); return false; }

        var content = await File.ReadAllTextAsync(filePath);
        var propsPath = Path.Combine(_workDir!, "eng", "centralpackagemanagement",
            "Directory.Generation.Packages.props");

        if (File.Exists(propsPath))
        {
            var props = await File.ReadAllTextAsync(propsPath);
            var m = Regex.Match(props,
                @"<UnbrandedGeneratorVersion>(.*?)</UnbrandedGeneratorVersion>");
            var targetVersion = m.Success ? m.Groups[1].Value : null;

            if (targetVersion is not null && issue.FilePath.Contains("http-client-csharp"))
            {
                var newContent = HttpClientCsharpVersionRegex().Replace(content,
                    $"\"@typespec/http-client-csharp\": \"{targetVersion}\"");

                if (newContent != content)
                {
                    await File.WriteAllTextAsync(filePath, newContent);
                    Log.Info($"Updated version in {issue.FilePath} to {targetVersion}");
                    return true;
                }
            }
        }

        Log.Warn($"Could not determine correct version for {issue.FilePath}");
        return false;
    }

    private async Task<bool> FixRegenAsync()
    {
        var script = Path.Combine(_workDir!, "eng", "packages",
            "http-client-csharp", "eng", "scripts", "Generate.ps1");
        if (!File.Exists(script)) { Log.Error("Generate.ps1 not found"); return false; }

        Log.Info("Running code regeneration...");
        var (exitCode, _, stderr) = await RunProcessAsync("pwsh",
            ["-File", script], _workDir!);

        if (exitCode != 0) { Log.Error($"Regeneration failed: {stderr[..Math.Min(500, stderr.Length)]}"); return false; }
        return true;
    }

    private async Task CommitAndPushAsync(PrAnalysis analysis, int fixCount)
    {
        await RunGitAsync("-C", _workDir!, "add", "-A");

        var (_, status, _) = await RunProcessAsync("git",
            ["-C", _workDir!, "status", "--porcelain"], _workDir!);
        if (string.IsNullOrWhiteSpace(status)) { Log.Info("No actual changes to commit"); return; }

        var msg = $"Fix {fixCount} issue(s) in upgrade PR\n\n"
                + "Automated fixes applied by upgrade-pr-agent:\n"
                + string.Join("\n", analysis.FixableIssues.Select(i => $"- {i.Description}"))
                + "\n\nCo-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>";

        await RunGitAsync("-C", _workDir!, "commit", "-m", msg);
        await RunGitAsync("-C", _workDir!, "push");
        Log.Info($"Pushed fixes to branch {analysis.HeadRef}");
    }

    private void Cleanup()
    {
        if (_workDir is not null && Directory.Exists(_workDir) && config.WorkspaceDir is null)
        {
            try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
        }
        _workDir = null;
    }

    private static async Task RunGitAsync(params string[] args)
    {
        var (exitCode, _, stderr) = await RunProcessAsync("git", args, null);
        if (exitCode != 0) throw new InvalidOperationException($"git failed: {stderr}");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string[] args, string? workingDir)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout.Trim(), stderr.Trim());
    }

    [GeneratedRegex(@"""@typespec/http-client-csharp"":\s*""[^""]*""")]
    private static partial Regex HttpClientCsharpVersionRegex();
}
