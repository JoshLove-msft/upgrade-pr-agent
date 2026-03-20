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

        if (analysis.HasMergeConflicts)
        {
            // Need full enough history for merge -- fetch both branches
            await RunGitAsync("clone", "--filter=blob:none",
                "--branch", analysis.HeadRef,
                "--single-branch", repoUrl, _workDir);
            await RunGitAsync("-C", _workDir, "fetch", "origin", "main");
        }
        else
        {
            await RunGitAsync("clone", "--depth", "1",
                "--branch", analysis.HeadRef,
                "--single-branch", repoUrl, _workDir);
        }
    }

    private async Task<bool> FixIssueAsync(FixableIssue issue) => issue.FixType switch
    {
        "merge_conflicts" => await FixMergeConflictsAsync(),
        "lockfile_stale" => await FixLockfileAsync(issue),
        "version_mismatch" => await FixVersionMismatchAsync(issue),
        "regen_needed" => await FixRegenAsync(),
        _ => false,
    };

    private async Task<bool> FixMergeConflictsAsync()
    {
        Log.Info("Attempting to merge main and resolve conflicts...");

        // Try merging main into the PR branch
        var (exitCode, _, _) = await RunProcessAsync("git",
            ["-C", _workDir!, "merge", "origin/main", "--no-edit"], _workDir!);

        if (exitCode == 0)
        {
            Log.Info("Merge succeeded with no conflicts");
            return true;
        }

        // Get list of conflicted files
        var (_, conflictOutput, _) = await RunProcessAsync("git",
            ["-C", _workDir!, "diff", "--name-only", "--diff-filter=U"], _workDir!);

        if (string.IsNullOrWhiteSpace(conflictOutput))
        {
            Log.Error("Merge failed but no conflicted files detected");
            await RunProcessAsync("git", ["-C", _workDir!, "merge", "--abort"], _workDir!);
            return false;
        }

        var conflictedFiles = conflictOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Log.Info($"Resolving conflicts in {conflictedFiles.Length} file(s)...");

        var allResolved = true;
        foreach (var file in conflictedFiles)
        {
            var filePath = Path.Combine(_workDir!, file.Trim());
            if (!File.Exists(filePath))
            {
                Log.Warn($"Conflicted file not found: {file}");
                allResolved = false;
                continue;
            }

            var content = await File.ReadAllTextAsync(filePath);
            var resolved = ResolveConflictsPickNewestVersion(content);

            if (resolved is null)
            {
                Log.Warn($"Could not auto-resolve conflicts in {file}");
                allResolved = false;
                continue;
            }

            await File.WriteAllTextAsync(filePath, resolved);
            await RunProcessAsync("git", ["-C", _workDir!, "add", file.Trim()], _workDir!);
            Log.Info($"Resolved conflicts in {file}");
        }

        if (!allResolved)
        {
            await RunProcessAsync("git", ["-C", _workDir!, "merge", "--abort"], _workDir!);
            return false;
        }

        // Complete the merge
        var (commitExit, _, commitErr) = await RunProcessAsync("git",
            ["-C", _workDir!, "-c", "core.editor=true", "merge", "--continue"], _workDir!);

        if (commitExit != 0)
        {
            // Try committing directly if merge --continue doesn't work
            var (commitExit2, _, _) = await RunProcessAsync("git",
                ["-C", _workDir!, "commit", "--no-edit"], _workDir!);
            if (commitExit2 != 0)
            {
                Log.Error($"Failed to complete merge: {commitErr}");
                return false;
            }
        }

        Log.Info("Merge conflicts resolved successfully");
        return true;
    }

    /// <summary>
    /// Resolves git conflict markers by picking the higher semver version
    /// for each conflict block. Returns null if any conflict can't be resolved.
    /// </summary>
    internal static string? ResolveConflictsPickNewestVersion(string content)
    {
        var lines = content.Split('\n');
        var result = new List<string>();
        var i = 0;

        while (i < lines.Length)
        {
            if (lines[i].TrimStart().StartsWith("<<<<<<<"))
            {
                var oursLines = new List<string>();
                var theirsLines = new List<string>();
                i++;

                // Collect "ours" (HEAD / PR branch)
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("======="))
                {
                    oursLines.Add(lines[i]);
                    i++;
                }
                if (i >= lines.Length) return null; // malformed
                i++; // skip =======

                // Collect "theirs" (main)
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(">>>>>>>"))
                {
                    theirsLines.Add(lines[i]);
                    i++;
                }
                if (i >= lines.Length) return null; // malformed
                i++; // skip >>>>>>>

                // Pick the block with the highest version
                var winner = PickBlockWithNewestVersions(oursLines, theirsLines);
                result.AddRange(winner);
            }
            else
            {
                result.Add(lines[i]);
                i++;
            }
        }

        return string.Join('\n', result);
    }

    /// <summary>
    /// Compare two conflict blocks and return the one containing the higher versions.
    /// Extracts all semver-like strings from each block, compares them pairwise,
    /// and picks the block where versions are newer.
    /// </summary>
    private static List<string> PickBlockWithNewestVersions(
        List<string> ours, List<string> theirs)
    {
        var oursVersions = ExtractVersions(ours);
        var theirsVersions = ExtractVersions(theirs);

        if (oursVersions.Count == 0 && theirsVersions.Count == 0)
        {
            // No versions found -- prefer ours (the PR branch)
            return ours;
        }

        // Compare the highest version in each block
        var oursMax = oursVersions.Count > 0 ? oursVersions.Max()! : "";
        var theirsMax = theirsVersions.Count > 0 ? theirsVersions.Max()! : "";

        return CompareVersionStrings(oursMax, theirsMax) >= 0 ? ours : theirs;
    }

    private static List<string> ExtractVersions(List<string> lines)
    {
        var versions = new List<string>();
        foreach (var line in lines)
        {
            foreach (Match m in SemverRegex().Matches(line))
                versions.Add(m.Value);
        }
        return versions;
    }

    /// <summary>
    /// Compares two version strings. Handles both semver (1.2.3) and
    /// pre-release (1.0.0-alpha.20260319.4) formats.
    /// Returns positive if a > b, negative if a < b, 0 if equal.
    /// </summary>
    private static int CompareVersionStrings(string a, string b)
    {
        if (a == b) return 0;
        if (string.IsNullOrEmpty(a)) return -1;
        if (string.IsNullOrEmpty(b)) return 1;

        // Split into base version and pre-release
        var aParts = a.Split('-', 2);
        var bParts = b.Split('-', 2);

        // Compare base version numerically
        var aSegments = aParts[0].Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var bSegments = bParts[0].Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();

        for (int j = 0; j < Math.Max(aSegments.Length, bSegments.Length); j++)
        {
            var av = j < aSegments.Length ? aSegments[j] : 0;
            var bv = j < bSegments.Length ? bSegments[j] : 0;
            if (av != bv) return av.CompareTo(bv);
        }

        // Same base version -- compare pre-release
        var aPre = aParts.Length > 1 ? aParts[1] : null;
        var bPre = bParts.Length > 1 ? bParts[1] : null;

        // No pre-release = release = higher than any pre-release
        if (aPre is null && bPre is not null) return 1;
        if (aPre is not null && bPre is null) return -1;
        if (aPre is null || bPre is null) return 0;

        // Compare pre-release segments (e.g. alpha.20260319.4 vs alpha.20260316.1)
        var aPreParts = aPre.Split('.');
        var bPreParts = bPre.Split('.');
        for (int j = 0; j < Math.Max(aPreParts.Length, bPreParts.Length); j++)
        {
            var ap = j < aPreParts.Length ? aPreParts[j] : "";
            var bp = j < bPreParts.Length ? bPreParts[j] : "";

            if (int.TryParse(ap, out var an) && int.TryParse(bp, out var bn))
            {
                if (an != bn) return an.CompareTo(bn);
            }
            else
            {
                var cmp = string.Compare(ap, bp, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
            }
        }

        return 0;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?")]
    private static partial Regex SemverRegex();

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
