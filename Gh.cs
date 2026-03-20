using System.Diagnostics;
using System.Text.Json;

namespace UpgradePrAgent;

/// <summary>Thin wrapper around the gh CLI.</summary>
public static class Gh
{
    public static async Task<string> RunAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            Log.Debug($"gh stderr: {stderr.Trim()}");

        return stdout.Trim();
    }

    public static async Task<JsonElement?> ApiAsync(
        string endpoint, string method = "GET",
        Dictionary<string, string>? fields = null,
        string? jqFilter = null, bool paginate = false)
    {
        var args = new List<string> { "api", endpoint, "--method", method };
        if (jqFilter is not null) { args.Add("--jq"); args.Add(jqFilter); }
        if (paginate) args.Add("--paginate");
        if (fields is not null)
        {
            foreach (var (k, v) in fields)
            {
                args.Add("-f"); args.Add($"{k}={v}");
            }
        }

        var raw = await RunAsync([.. args]);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try { return JsonDocument.Parse(raw).RootElement; }
        catch (JsonException) { return null; }
    }

    public static async Task<List<JsonElement>> ListOpenPrsAsync(
        string owner, string repo, string author, string searchQuery)
    {
        var raw = await RunAsync(
            "search", "prs",
            "--repo", $"{owner}/{repo}",
            "--author", author,
            "--state", "open",
            "--json", "number,title,state,labels,createdAt",
            "--limit", "20",
            "--", searchQuery);

        if (string.IsNullOrWhiteSpace(raw)) return [];

        var doc = JsonDocument.Parse(raw);
        return [.. doc.RootElement.EnumerateArray()];
    }

    public static async Task<JsonElement> GetPrAsync(string owner, string repo, int number) =>
        (await ApiAsync($"/repos/{owner}/{repo}/pulls/{number}"))!.Value;

    public static async Task<List<JsonElement>> GetCheckRunsAsync(string owner, string repo, string sha)
    {
        var el = await ApiAsync($"/repos/{owner}/{repo}/commits/{sha}/check-runs",
            jqFilter: ".check_runs");
        if (el is null) return [];
        return [.. el.Value.EnumerateArray()];
    }

    public static async Task<List<JsonElement>> GetPrReviewsAsync(string owner, string repo, int number)
    {
        var el = await ApiAsync($"/repos/{owner}/{repo}/pulls/{number}/reviews", paginate: true);
        if (el is null) return [];
        return [.. el.Value.EnumerateArray()];
    }

    public static async Task<List<JsonElement>> GetPrReviewCommentsAsync(string owner, string repo, int number)
    {
        var el = await ApiAsync($"/repos/{owner}/{repo}/pulls/{number}/comments", paginate: true);
        if (el is null) return [];
        return [.. el.Value.EnumerateArray()];
    }

    public static async Task<string> ApprovePrAsync(string owner, string repo, int number, string body = "")
    {
        var args = new List<string>
        {
            "pr", "review", number.ToString(), "--approve",
            "--repo", $"{owner}/{repo}"
        };
        if (!string.IsNullOrEmpty(body)) { args.Add("--body"); args.Add(body); }
        return await RunAsync([.. args]);
    }

    public static async Task<string> MergePrAsync(string owner, string repo, int number, string method = "squash") =>
        await RunAsync("pr", "merge", number.ToString(), $"--{method}",
            "--repo", $"{owner}/{repo}", "--auto");

    public static async Task AddCommentAsync(string owner, string repo, int number, string body) =>
        await ApiAsync($"/repos/{owner}/{repo}/issues/{number}/comments",
            method: "POST", fields: new() { ["body"] = body });
}
