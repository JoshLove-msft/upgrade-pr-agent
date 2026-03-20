using System.Text.Json;
using System.Text.RegularExpressions;

namespace UpgradePrAgent;

public enum PrStatus
{
    WaitingForCi,
    CiFailing,
    CiPassed,
    HasReviewIssues,
    ReadyToApprove,
    Approved,
    ReadyToMerge,
    Merged,
    Closed,
    Superseded,
    Unknown,
}

public sealed record FixableIssue(string Description, string FilePath, string FixType,
    Dictionary<string, string?>? Details = null);

public sealed class PrAnalysis
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string HeadSha { get; init; } = "";
    public string HeadRef { get; init; } = "";
    public PrStatus Status { get; set; } = PrStatus.Unknown;
    public bool CiPassed { get; set; }
    public bool CiPending { get; set; }
    public List<string> CiFailedChecks { get; } = [];
    public bool HasApprovals { get; set; }
    public int ApprovalCount { get; set; }
    public List<FixableIssue> FixableIssues { get; } = [];
    public List<string> UnfixableIssues { get; } = [];
    public bool IsSuperseded { get; set; }
    public int? NewerPrNumber { get; set; }
    public bool Mergeable { get; set; }
    public bool HasMergeConflicts { get; set; }
}

public static partial class Analyzer
{
    public static async Task<PrAnalysis> AnalyzePrAsync(
        AgentConfig config, int prNumber, List<JsonElement>? allOpenPrs = null)
    {
        var pr = await Gh.GetPrAsync(config.Owner, config.Repo, prNumber);

        if (pr.TryGetProperty("merged", out var merged) && merged.GetBoolean())
            return new PrAnalysis
            {
                Number = prNumber, Title = pr.GetProperty("title").GetString()!,
                HeadSha = pr.GetProperty("head").GetProperty("sha").GetString()!,
                HeadRef = pr.GetProperty("head").GetProperty("ref").GetString()!,
                Status = PrStatus.Merged,
            };

        if (pr.GetProperty("state").GetString() == "closed")
            return new PrAnalysis
            {
                Number = prNumber, Title = pr.GetProperty("title").GetString()!,
                HeadSha = pr.GetProperty("head").GetProperty("sha").GetString()!,
                HeadRef = pr.GetProperty("head").GetProperty("ref").GetString()!,
                Status = PrStatus.Closed,
            };

        var analysis = new PrAnalysis
        {
            Number = prNumber,
            Title = pr.GetProperty("title").GetString()!,
            HeadSha = pr.GetProperty("head").GetProperty("sha").GetString()!,
            HeadRef = pr.GetProperty("head").GetProperty("ref").GetString()!,
            Mergeable = pr.TryGetProperty("mergeable_state", out var ms)
                        && ms.GetString() == "clean",
            HasMergeConflicts = pr.TryGetProperty("mergeable_state", out var ms2)
                                && ms2.GetString() == "dirty",
        };

        CheckSuperseded(config, analysis, pr, allOpenPrs);
        if (analysis.IsSuperseded)
        {
            analysis.Status = PrStatus.Superseded;
            return analysis;
        }

        await CheckCiAsync(config, analysis);
        await CheckReviewsAsync(config, analysis);

        // Detect merge conflicts as a fixable issue
        if (analysis.HasMergeConflicts)
            analysis.FixableIssues.Insert(0, new FixableIssue(
                "Merge conflicts with base branch (will resolve using newest versions)",
                "", "merge_conflicts"));

        // Fixable issues take priority -- the CI failure may be caused by them
        if (analysis.FixableIssues.Count > 0)
            analysis.Status = PrStatus.HasReviewIssues;
        else if (analysis.CiPending)
            analysis.Status = PrStatus.WaitingForCi;
        else if (!analysis.CiPassed)
            analysis.Status = PrStatus.CiFailing;
        else if (analysis.HasApprovals)
            analysis.Status = PrStatus.ReadyToMerge;
        else
            analysis.Status = PrStatus.ReadyToApprove;

        return analysis;
    }

    private static void CheckSuperseded(
        AgentConfig config, PrAnalysis analysis, JsonElement pr,
        List<JsonElement>? allOpenPrs)
    {
        if (allOpenPrs is null) return;

        var title = pr.GetProperty("title").GetString()!;
        foreach (var pattern in config.PrTitlePatterns)
        {
            if (!title.Contains(pattern)) continue;

            var newer = allOpenPrs
                .Where(p => p.GetProperty("title").GetString()!.Contains(pattern)
                            && p.GetProperty("number").GetInt32() > analysis.Number)
                .OrderByDescending(p => p.GetProperty("number").GetInt32())
                .FirstOrDefault();

            if (newer.ValueKind != JsonValueKind.Undefined)
            {
                analysis.IsSuperseded = true;
                analysis.NewerPrNumber = newer.GetProperty("number").GetInt32();
                Log.Info($"PR #{analysis.Number} is superseded by #{analysis.NewerPrNumber}");
            }
            break;
        }
    }

    private static async Task CheckCiAsync(AgentConfig config, PrAnalysis analysis)
    {
        var checks = await Gh.GetCheckRunsAsync(config.Owner, config.Repo, analysis.HeadSha);
        if (checks.Count == 0) { analysis.CiPending = true; return; }

        var mainCheck = checks.FirstOrDefault(c =>
            c.GetProperty("name").GetString() == config.RequiredCheckPattern);

        if (mainCheck.ValueKind != JsonValueKind.Undefined)
        {
            var status = mainCheck.GetProperty("status").GetString();
            var conclusion = mainCheck.TryGetProperty("conclusion", out var cc) ? cc.GetString() : null;

            if (status != "completed")
                analysis.CiPending = true;
            else if (conclusion == "success")
                analysis.CiPassed = true;
            else
            {
                analysis.CiPassed = false;
                analysis.CiFailedChecks.Add($"{mainCheck.GetProperty("name").GetString()}: {conclusion}");
            }
        }
        else
        {
            var completed = checks.Where(c => c.GetProperty("status").GetString() == "completed").ToList();
            var pending = checks.Where(c => c.GetProperty("status").GetString() != "completed").ToList();
            var failed = completed.Where(c =>
                c.TryGetProperty("conclusion", out var cc) && cc.GetString() != "success").ToList();

            if (pending.Count > 0) analysis.CiPending = true;
            else if (failed.Count > 0)
            {
                analysis.CiPassed = false;
                foreach (var c in failed)
                {
                    var name = c.GetProperty("name").GetString();
                    var conclusion = c.TryGetProperty("conclusion", out var cc2) && cc2.GetString() is string s ? s : "unknown";
                    analysis.CiFailedChecks.Add($"{name}: {conclusion}");
                }
            }
            else analysis.CiPassed = true;
        }
    }

    private static async Task CheckReviewsAsync(AgentConfig config, PrAnalysis analysis)
    {
        var reviews = await Gh.GetPrReviewsAsync(config.Owner, config.Repo, analysis.Number);
        var comments = await Gh.GetPrReviewCommentsAsync(config.Owner, config.Repo, analysis.Number);

        analysis.ApprovalCount = reviews.Count(r =>
            r.GetProperty("state").GetString() == "APPROVED");
        analysis.HasApprovals = analysis.ApprovalCount > 0;

        foreach (var comment in comments)
        {
            var body = comment.GetProperty("body").GetString() ?? "";
            var path = comment.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            ClassifyReviewComment(analysis, body, path);
        }
    }

    private static void ClassifyReviewComment(PrAnalysis analysis, string body, string path)
    {
        var lower = body.ToLowerInvariant();

        if (lower.Contains("package-lock") &&
            (lower.Contains("stale") || lower.Contains("still pins") ||
             lower.Contains("regenerate") || lower.Contains("update")))
        {
            analysis.FixableIssues.Add(new FixableIssue(
                "package-lock.json is out of sync with package.json",
                path, "lockfile_stale"));
            return;
        }

        if ((lower.Contains("bump") || lower.Contains("version")) &&
            (lower.Contains("mismatch") || lower.Contains("still") || lower.Contains("please")))
        {
            var match = VersionRegex().Match(body);
            analysis.FixableIssues.Add(new FixableIssue(
                $"Version needs updating in {path}", path, "version_mismatch",
                new() { ["suggested_version"] = match.Success ? match.Groups[1].Value : null }));
            return;
        }

        if (lower.Contains("devdependencies") || lower.Contains("dev dependencies"))
        {
            analysis.FixableIssues.Add(new FixableIssue(
                "devDependencies version needs bumping", path, "version_mismatch"));
            return;
        }

        if (body.Length > 50)
            analysis.UnfixableIssues.Add($"[{path}] {body[..Math.Min(200, body.Length)]}");
    }

    [GeneratedRegex(@"(\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?)")]
    private static partial Regex VersionRegex();
}
