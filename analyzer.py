"""Analyze upgrade PRs to determine status and needed actions."""

import logging
import re
from dataclasses import dataclass, field
from enum import Enum

import gh
from config import AgentConfig

log = logging.getLogger(__name__)


class PRStatus(Enum):
    WAITING_FOR_CI = "waiting_for_ci"
    CI_FAILING = "ci_failing"
    CI_PASSED = "ci_passed"
    HAS_REVIEW_ISSUES = "has_review_issues"
    READY_TO_APPROVE = "ready_to_approve"
    APPROVED = "approved"
    READY_TO_MERGE = "ready_to_merge"
    MERGED = "merged"
    CLOSED = "closed"
    SUPERSEDED = "superseded"
    UNKNOWN = "unknown"


@dataclass
class FixableIssue:
    description: str
    file_path: str
    fix_type: str  # "version_mismatch", "lockfile_stale", "regen_needed"
    details: dict = field(default_factory=dict)


@dataclass
class PRAnalysis:
    number: int
    title: str
    head_sha: str
    head_ref: str
    status: PRStatus
    ci_passed: bool = False
    ci_pending: bool = False
    ci_failed_checks: list[str] = field(default_factory=list)
    has_approvals: bool = False
    approval_count: int = 0
    fixable_issues: list[FixableIssue] = field(default_factory=list)
    unfixable_issues: list[str] = field(default_factory=list)
    is_superseded: bool = False
    newer_pr_number: int | None = None
    mergeable: bool = False


def analyze_pr(config: AgentConfig, pr_number: int,
               all_open_prs: list[dict] | None = None) -> PRAnalysis:
    """Fully analyze an upgrade PR and determine what action to take."""
    pr = gh.get_pr(config.owner, config.repo, pr_number)

    if pr.get("merged"):
        return PRAnalysis(
            number=pr_number, title=pr["title"],
            head_sha=pr["head"]["sha"], head_ref=pr["head"]["ref"],
            status=PRStatus.MERGED,
        )
    if pr.get("state") == "closed":
        return PRAnalysis(
            number=pr_number, title=pr["title"],
            head_sha=pr["head"]["sha"], head_ref=pr["head"]["ref"],
            status=PRStatus.CLOSED,
        )

    analysis = PRAnalysis(
        number=pr_number,
        title=pr["title"],
        head_sha=pr["head"]["sha"],
        head_ref=pr["head"]["ref"],
        status=PRStatus.UNKNOWN,
        mergeable=pr.get("mergeable_state") == "clean",
    )

    # Check if superseded by a newer PR with the same pattern
    _check_superseded(config, analysis, pr, all_open_prs)
    if analysis.is_superseded:
        analysis.status = PRStatus.SUPERSEDED
        return analysis

    # Check CI status
    _check_ci(config, analysis)

    # Check reviews and comments for issues
    _check_reviews(config, analysis)

    # Determine final status
    # Fixable issues take priority over CI failure — the failure may be
    # *caused* by the issues (e.g. stale lockfile → npm ci mismatch).
    if analysis.fixable_issues:
        analysis.status = PRStatus.HAS_REVIEW_ISSUES
    elif analysis.ci_pending:
        analysis.status = PRStatus.WAITING_FOR_CI
    elif not analysis.ci_passed:
        analysis.status = PRStatus.CI_FAILING
    elif analysis.has_approvals:
        analysis.status = PRStatus.READY_TO_MERGE
    else:
        analysis.status = PRStatus.READY_TO_APPROVE

    return analysis


def _check_superseded(config: AgentConfig, analysis: PRAnalysis,
                      pr: dict, all_open_prs: list[dict] | None):
    """Check if a newer PR with the same upgrade pattern exists."""
    if not all_open_prs:
        return

    # Extract version pattern from title, e.g. "Update UnbrandedGeneratorVersion to X"
    title = pr["title"]
    for pattern in config.pr_title_patterns:
        if pattern in title:
            # Find other open PRs with the same prefix
            same_type = [
                p for p in all_open_prs
                if pattern in p.get("title", "")
                and p["number"] != analysis.number
                and p["number"] > analysis.number  # newer = higher number
            ]
            if same_type:
                newest = max(same_type, key=lambda p: p["number"])
                analysis.is_superseded = True
                analysis.newer_pr_number = newest["number"]
                log.info("PR #%d is superseded by #%d",
                         analysis.number, newest["number"])
            break


def _check_ci(config: AgentConfig, analysis: PRAnalysis):
    """Check CI check-run status on the head commit."""
    checks = gh.get_check_runs(config.owner, config.repo, analysis.head_sha)

    if not checks:
        analysis.ci_pending = True
        return

    # Find the main CI check (or evaluate all)
    main_check = None
    for check in checks:
        name = check.get("name", "")
        if name == config.required_check_pattern:
            main_check = check
            break

    if main_check:
        status = main_check.get("status")
        conclusion = main_check.get("conclusion")
        if status != "completed":
            analysis.ci_pending = True
        elif conclusion == "success":
            analysis.ci_passed = True
        else:
            analysis.ci_passed = False
            analysis.ci_failed_checks.append(
                f"{main_check['name']}: {conclusion}"
            )
    else:
        # Evaluate all checks
        completed = [c for c in checks if c.get("status") == "completed"]
        pending = [c for c in checks if c.get("status") != "completed"]
        failed = [c for c in completed if c.get("conclusion") != "success"]

        if pending:
            analysis.ci_pending = True
        elif failed:
            analysis.ci_passed = False
            for c in failed:
                analysis.ci_failed_checks.append(
                    f"{c['name']}: {c.get('conclusion')}"
                )
        else:
            analysis.ci_passed = True


def _check_reviews(config: AgentConfig, analysis: PRAnalysis):
    """Check reviews and review comments for issues and approvals."""
    reviews = gh.get_pr_reviews(config.owner, config.repo, analysis.number)
    comments = gh.get_pr_review_comments(
        config.owner, config.repo, analysis.number
    )

    # Count approvals
    approvals = [r for r in reviews if r.get("state") == "APPROVED"]
    analysis.approval_count = len(approvals)
    analysis.has_approvals = len(approvals) > 0

    # Analyze review comments for fixable issues
    for comment in comments:
        body = comment.get("body", "")
        path = comment.get("path", "")
        _classify_review_comment(analysis, body, path)


def _classify_review_comment(analysis: PRAnalysis, body: str, path: str):
    """Classify a review comment as fixable or not."""
    body_lower = body.lower()

    # Pattern: package-lock.json out of sync
    if ("package-lock" in body_lower and
            ("stale" in body_lower or "still pins" in body_lower or
             "regenerate" in body_lower or "update" in body_lower)):
        analysis.fixable_issues.append(FixableIssue(
            description="package-lock.json is out of sync with package.json",
            file_path=path,
            fix_type="lockfile_stale",
        ))
        return

    # Pattern: version mismatch between files
    if ("bump" in body_lower or "version" in body_lower) and (
            "mismatch" in body_lower or "still" in body_lower or
            "please" in body_lower):
        # Extract version info if possible
        version_match = re.search(r'(\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?)',
                                  body)
        analysis.fixable_issues.append(FixableIssue(
            description=f"Version needs updating in {path}",
            file_path=path,
            fix_type="version_mismatch",
            details={
                "suggested_version": version_match.group(1)
                if version_match else None,
            },
        ))
        return

    # Pattern: devDependencies not bumped
    if "devdependencies" in body_lower or "dev dependencies" in body_lower:
        analysis.fixable_issues.append(FixableIssue(
            description="devDependencies version needs bumping",
            file_path=path,
            fix_type="version_mismatch",
        ))
        return

    # If we can't classify it, it's unfixable (needs human review)
    if len(body) > 50:  # Ignore trivial bot messages
        analysis.unfixable_issues.append(
            f"[{path}] {body[:200]}"
        )
