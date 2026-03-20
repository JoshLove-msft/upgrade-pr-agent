"""Configuration for the upgrade PR agent."""

from dataclasses import dataclass, field


@dataclass
class AgentConfig:
    # Repository to watch
    owner: str = "Azure"
    repo: str = "azure-sdk-for-net"

    # PR matching criteria
    pr_author: str = "azure-sdk"
    pr_title_patterns: list[str] = field(default_factory=lambda: [
        "Update UnbrandedGeneratorVersion",
        "Update AzureGeneratorVersion",
        "Update AzureManagementGeneratorVersion",
    ])
    pr_labels: list[str] = field(default_factory=lambda: ["CodeGen"])

    # Behavior
    dry_run: bool = True  # Set False to actually approve/merge
    poll_interval_seconds: int = 300  # 5 minutes
    max_fix_attempts: int = 3
    auto_approve: bool = True
    auto_merge: bool = True
    merge_method: str = "squash"  # squash, merge, rebase

    # CI settings
    required_check_pattern: str = "net - pullrequest"
    ci_timeout_minutes: int = 120

    # Local workspace for cloning/fixing
    workspace_dir: str = ""

    # Logging
    log_file: str = "upgrade-pr-agent.log"
    verbose: bool = False
