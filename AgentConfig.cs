namespace UpgradePrAgent;

public sealed class AgentConfig
{
    public string Owner { get; set; } = "Azure";
    public string Repo { get; set; } = "azure-sdk-for-net";
    public string PrAuthor { get; set; } = "azure-sdk";

    public List<string> PrTitlePatterns { get; set; } =
    [
        "Update UnbrandedGeneratorVersion",
        "Update AzureGeneratorVersion",
        "Update AzureManagementGeneratorVersion",
    ];

    public bool DryRun { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 300;
    public bool AutoApprove { get; set; } = true;
    public bool AutoMerge { get; set; } = true;
    public string MergeMethod { get; set; } = "squash";
    public string RequiredCheckPattern { get; set; } = "net - pullrequest";
    public string? WorkspaceDir { get; set; }
    public bool Verbose { get; set; }
}
