using System.Text.Json;

namespace UpgradePrAgent;

public static class Program
{
    private static readonly CancellationTokenSource s_cts = new();

    public static async Task<int> Main(string[] args)
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; s_cts.Cancel(); };

        var config = ParseArgs(args);
        if (config is null) return 1;

        Log.Verbose = config.Verbose;

        if (TryGetArg(args, "--pr", out var prStr) && int.TryParse(prStr, out var prNumber))
        {
            Console.WriteLine($"\n  Processing single PR #{prNumber}...\n");
            await ProcessPrAsync(config, prNumber);
            return 0;
        }

        if (args.Contains("--once"))
        {
            await RunCycleAsync(config);
            return 0;
        }

        Console.WriteLine("\n  Upgrade PR Agent starting (Ctrl+C to stop)\n");
        while (!s_cts.IsCancellationRequested)
        {
            try { await RunCycleAsync(config); }
            catch (Exception ex) { Log.Error($"Unhandled error in cycle: {ex.Message}"); }

            if (s_cts.IsCancellationRequested) break;

            Log.Info($"Sleeping {config.PollIntervalSeconds} seconds...");
            try { await Task.Delay(config.PollIntervalSeconds * 1000, s_cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        Console.WriteLine("\n  Agent stopped.\n");
        return 0;
    }

    private static async Task<List<JsonElement>> FindUpgradePrsAsync(AgentConfig config)
    {
        var all = new List<JsonElement>();
        var seen = new HashSet<int>();

        foreach (var pattern in config.PrTitlePatterns)
        {
            var prs = await Gh.ListOpenPrsAsync(
                config.Owner, config.Repo, config.PrAuthor, pattern);

            foreach (var pr in prs)
            {
                var num = pr.GetProperty("number").GetInt32();
                if (seen.Add(num)) all.Add(pr);
            }
        }

        all.Sort((a, b) => b.GetProperty("number").GetInt32()
                                .CompareTo(a.GetProperty("number").GetInt32()));
        return all;
    }

    private static async Task RunCycleAsync(AgentConfig config)
    {
        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
        Console.WriteLine($"\n{"=",1}{new string('=', 59)}");
        Console.WriteLine($"  Upgrade PR Agent -- cycle at {ts}");
        Console.WriteLine($"  {config.Owner}/{config.Repo}  " +
                          $"{(config.DryRun ? "[DRY RUN]" : "[LIVE]")}");
        Console.WriteLine($"{"=",1}{new string('=', 59)}\n");

        var prs = await FindUpgradePrsAsync(config);
        if (prs.Count == 0)
        {
            Console.WriteLine("  No open upgrade PRs found.\n");
            return;
        }

        Console.WriteLine($"  Found {prs.Count} open upgrade PR(s):\n");

        foreach (var pr in prs)
        {
            try
            {
                await ProcessPrAsync(config, pr.GetProperty("number").GetInt32(), prs);
            }
            catch (Exception ex)
            {
                Log.Error($"Error processing PR #{pr.GetProperty("number").GetInt32()}: {ex.Message}");
            }
        }
    }

    private static async Task<PrAnalysis> ProcessPrAsync(
        AgentConfig config, int prNumber, List<JsonElement>? allOpenPrs = null)
    {
        var analysis = await Analyzer.AnalyzePrAsync(config, prNumber, allOpenPrs);
        PrintStatus(analysis);

        if (analysis.Status == PrStatus.Superseded)
        {
            Log.Info($"PR #{prNumber} superseded by #{analysis.NewerPrNumber} -- closing");
            if (config.DryRun)
                Log.Info($"[DRY RUN] Would close PR #{prNumber}");
            else
            {
                await Gh.AddCommentAsync(config.Owner, config.Repo, prNumber,
                    $"Closing -- superseded by #{analysis.NewerPrNumber}.");
                await Gh.ClosePrAsync(config.Owner, config.Repo, prNumber);
            }
            return analysis;
        }

        if (analysis.Status == PrStatus.WaitingForCi)
        {
            Log.Info($"PR #{prNumber} -- CI still running, will check again later");
            return analysis;
        }

        if (analysis.Status == PrStatus.CiFailing)
        {
            Log.Warn($"PR #{prNumber} -- CI failing: {string.Join(", ", analysis.CiFailedChecks)}");
            if (analysis.FixableIssues.Count == 0) return analysis;
            // Fall through to fix attempt -- CI may be failing because of the issues
        }

        if (analysis.Status is PrStatus.HasReviewIssues or PrStatus.CiFailing
            && analysis.FixableIssues.Count > 0)
        {
            Log.Info($"PR #{prNumber} -- attempting fixes...");
            var fixer = new PrFixer(config);
            if (await fixer.FixIssuesAsync(analysis))
                Log.Info($"PR #{prNumber} -- fixes applied, CI will re-run");
            else
                Log.Warn($"PR #{prNumber} -- some issues could not be auto-fixed");
            return analysis;
        }

        if (analysis.Status == PrStatus.ReadyToApprove && config.AutoApprove)
        {
            if (config.DryRun)
                Log.Info($"[DRY RUN] Would approve PR #{prNumber}");
            else
            {
                Log.Info($"Approving PR #{prNumber}");
                await Gh.ApprovePrAsync(config.Owner, config.Repo, prNumber,
                    "Automated approval -- CI passed, upgrade looks good.");
                analysis.HasApprovals = true;
                analysis.Status = PrStatus.ReadyToMerge;
            }
        }

        if (analysis.Status is PrStatus.ReadyToMerge or PrStatus.Approved && config.AutoMerge)
        {
            if (config.DryRun)
                Log.Info($"[DRY RUN] Would merge PR #{prNumber} ({config.MergeMethod})");
            else
            {
                Log.Info($"Merging PR #{prNumber} ({config.MergeMethod})");
                await Gh.MergePrAsync(config.Owner, config.Repo, prNumber, config.MergeMethod);
                analysis.Status = PrStatus.Merged;
            }
        }

        return analysis;
    }

    private static void PrintStatus(PrAnalysis analysis)
    {
        var (tag, color) = analysis.Status switch
        {
            PrStatus.WaitingForCi => ("[WAIT]", ConsoleColor.Yellow),
            PrStatus.CiFailing => ("[FAIL]", ConsoleColor.Red),
            PrStatus.CiPassed => ("[ OK ]", ConsoleColor.Green),
            PrStatus.HasReviewIssues => ("[FIX ]", ConsoleColor.Magenta),
            PrStatus.ReadyToApprove => ("[APPR]", ConsoleColor.Cyan),
            PrStatus.Approved => ("[APPR]", ConsoleColor.Cyan),
            PrStatus.ReadyToMerge => ("[MERG]", ConsoleColor.Green),
            PrStatus.Merged => ("[DONE]", ConsoleColor.DarkGray),
            PrStatus.Closed => ("[CLOS]", ConsoleColor.DarkGray),
            PrStatus.Superseded => ("[SKIP]", ConsoleColor.DarkGray),
            _ => ("[ ?? ]", ConsoleColor.Gray),
        };

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write($"  {tag}");
        Console.ForegroundColor = prev;
        Console.WriteLine($" #{analysis.Number}  {analysis.Status}  {analysis.Title}");

        foreach (var issue in analysis.FixableIssues)
            Console.WriteLine($"         -> FIX: {issue.Description}");
        foreach (var check in analysis.CiFailedChecks.Take(3))
            Console.WriteLine($"         -> FAIL: {check}");
        if (analysis.IsSuperseded && analysis.NewerPrNumber is not null)
            Console.WriteLine($"         -> Newer: #{analysis.NewerPrNumber}");
        Console.WriteLine();
    }

    private static AgentConfig? ParseArgs(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
            Usage: UpgradePrAgent [options]

            Options:
              --live            Actually approve/merge (default is dry-run)
              --once            Run one cycle then exit
              --pr <number>     Process a single PR number
              --interval <sec>  Poll interval in seconds (default: 300)
              --owner <owner>   GitHub org/owner (default: Azure)
              --repo <repo>     Repository name (default: azure-sdk-for-net)
              -v, --verbose     Verbose logging
              -h, --help        Show this help
            """);
            return null;
        }

        return new AgentConfig
        {
            DryRun = !args.Contains("--live"),
            Verbose = args.Contains("-v") || args.Contains("--verbose"),
            PollIntervalSeconds = TryGetArg(args, "--interval", out var iv)
                && int.TryParse(iv, out var interval) ? interval : 300,
            Owner = TryGetArg(args, "--owner", out var owner) ? owner : "Azure",
            Repo = TryGetArg(args, "--repo", out var repo) ? repo : "azure-sdk-for-net",
        };
    }

    private static bool TryGetArg(string[] args, string key, out string value)
    {
        value = "";
        var idx = Array.IndexOf(args, key);
        if (idx < 0 || idx + 1 >= args.Length) return false;
        value = args[idx + 1];
        return true;
    }
}
