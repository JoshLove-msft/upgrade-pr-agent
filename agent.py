#!/usr/bin/env python3
"""
Upgrade PR Agent — continuously monitors a GitHub repo for automated
dependency-upgrade PRs, fixes common issues, approves, and merges them.

Usage:
    python agent.py                     # dry-run mode (default)
    python agent.py --live              # actually approve & merge
    python agent.py --once              # run once then exit
    python agent.py --pr 57270          # process a single PR
"""

import argparse
import logging
import signal
import sys
import time
from datetime import datetime, timezone

import gh
from analyzer import PRAnalysis, PRStatus, analyze_pr
from config import AgentConfig
from fixer import PRFixer

log = logging.getLogger("upgrade-pr-agent")

# ── Colour helpers for terminal output ──────────────────────────────────────

COLOURS = {
    PRStatus.WAITING_FOR_CI: "\033[33m",      # yellow
    PRStatus.CI_FAILING: "\033[31m",           # red
    PRStatus.CI_PASSED: "\033[32m",            # green
    PRStatus.HAS_REVIEW_ISSUES: "\033[35m",    # magenta
    PRStatus.READY_TO_APPROVE: "\033[36m",     # cyan
    PRStatus.APPROVED: "\033[36m",
    PRStatus.READY_TO_MERGE: "\033[32;1m",     # bold green
    PRStatus.MERGED: "\033[90m",               # grey
    PRStatus.CLOSED: "\033[90m",
    PRStatus.SUPERSEDED: "\033[90m",
    PRStatus.UNKNOWN: "\033[37m",
}
RESET = "\033[0m"

_running = True


def _sigint(sig, frame):
    global _running
    log.info("Received interrupt -- shutting down after current cycle")
    _running = False


signal.signal(signal.SIGINT, _sigint)
signal.signal(signal.SIGTERM, _sigint)


# ── Core agent loop ─────────────────────────────────────────────────────────

def find_upgrade_prs(config: AgentConfig) -> list[dict]:
    """Find all open upgrade PRs matching our patterns."""
    all_prs = []
    for pattern in config.pr_title_patterns:
        prs = gh.list_open_prs(
            config.owner, config.repo, config.pr_author,
            search_query=pattern,
        )
        all_prs.extend(prs)

    # Deduplicate by number
    seen = set()
    unique = []
    for pr in all_prs:
        if pr["number"] not in seen:
            seen.add(pr["number"])
            unique.append(pr)

    unique.sort(key=lambda p: p["number"], reverse=True)
    return unique


def process_pr(config: AgentConfig, pr_number: int,
               all_open_prs: list[dict] | None = None) -> PRAnalysis:
    """Analyze and act on a single PR."""
    analysis = analyze_pr(config, pr_number, all_open_prs)
    _print_status(analysis)

    # ── State machine ───────────────────────────────────────────────────
    if analysis.status == PRStatus.SUPERSEDED:
        log.info("PR #%d superseded by #%d — skipping",
                 pr_number, analysis.newer_pr_number)
        if not config.dry_run:
            gh.add_comment(
                config.owner, config.repo, pr_number,
                f"Closing -- superseded by #{analysis.newer_pr_number}."
            )
        return analysis

    if analysis.status == PRStatus.WAITING_FOR_CI:
        log.info("PR #%d — CI still running, will check again later",
                 pr_number)
        return analysis

    if analysis.status == PRStatus.CI_FAILING:
        log.warning("PR #%d — CI failing: %s",
                     pr_number, ", ".join(analysis.ci_failed_checks))
        # Don't return yet — fall through to check fixable issues too
        if not analysis.fixable_issues:
            return analysis

    if analysis.status in (PRStatus.HAS_REVIEW_ISSUES, PRStatus.CI_FAILING):
        log.info("PR #%d -- attempting fixes...", pr_number)
        fixer = PRFixer(config)
        if fixer.fix_issues(analysis):
            log.info("PR #%d — fixes applied, CI will re-run", pr_number)
        else:
            log.warning("PR #%d — some issues could not be auto-fixed",
                        pr_number)
        return analysis

    if analysis.status == PRStatus.READY_TO_APPROVE:
        if config.auto_approve:
            if config.dry_run:
                log.info("[DRY RUN] Would approve PR #%d", pr_number)
            else:
                log.info("Approving PR #%d", pr_number)
                gh.approve_pr(
                    config.owner, config.repo, pr_number,
                    body="Automated approval -- CI passed, "
                         "upgrade looks good.",
                )
                analysis.has_approvals = True
                analysis.status = PRStatus.READY_TO_MERGE

    if analysis.status in (PRStatus.READY_TO_MERGE, PRStatus.APPROVED):
        if config.auto_merge:
            if config.dry_run:
                log.info("[DRY RUN] Would merge PR #%d (%s)",
                         pr_number, config.merge_method)
            else:
                log.info("Merging PR #%d (%s)", pr_number, config.merge_method)
                gh.merge_pr(config.owner, config.repo, pr_number,
                            config.merge_method)
                analysis.status = PRStatus.MERGED

    return analysis


def run_cycle(config: AgentConfig):
    """Run one full check cycle across all matching PRs."""
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    print(f"\n{'=' * 60}")
    print(f"  Upgrade PR Agent -- cycle at {ts}")
    print(f"  {config.owner}/{config.repo}  "
          f"{'[DRY RUN]' if config.dry_run else '[LIVE]'}")
    print(f"{'=' * 60}\n")

    prs = find_upgrade_prs(config)
    if not prs:
        print("  No open upgrade PRs found.\n")
        return

    print(f"  Found {len(prs)} open upgrade PR(s):\n")

    for pr in prs:
        try:
            process_pr(config, pr["number"], all_open_prs=prs)
        except Exception:
            log.exception("Error processing PR #%d", pr["number"])


def _print_status(analysis: PRAnalysis):
    """Pretty-print PR status to terminal."""
    colour = COLOURS.get(analysis.status, "")
    sym = {
        PRStatus.WAITING_FOR_CI: "[WAIT]",
        PRStatus.CI_FAILING: "[FAIL]",
        PRStatus.CI_PASSED: "[ OK ]",
        PRStatus.HAS_REVIEW_ISSUES: "[FIX ]",
        PRStatus.READY_TO_APPROVE: "[APPR]",
        PRStatus.APPROVED: "[APPR]",
        PRStatus.READY_TO_MERGE: "[MERG]",
        PRStatus.MERGED: "[DONE]",
        PRStatus.CLOSED: "[CLOS]",
        PRStatus.SUPERSEDED: "[SKIP]",
        PRStatus.UNKNOWN: "[ ?? ]",
    }.get(analysis.status, "[ ?? ]")

    print(f"  {sym} #{analysis.number}  "
          f"{colour}{analysis.status.value}{RESET}  "
          f"{analysis.title}")

    if analysis.fixable_issues:
        for issue in analysis.fixable_issues:
            print(f"         -> FIX: {issue.description}")
    if analysis.ci_failed_checks:
        for check in analysis.ci_failed_checks[:3]:
            print(f"         -> FAIL: {check}")
    if analysis.is_superseded and analysis.newer_pr_number:
        print(f"         -> Newer: #{analysis.newer_pr_number}")
    print()


# ── Entrypoint ──────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Monitor and auto-merge upgrade PRs"
    )
    parser.add_argument("--live", action="store_true",
                        help="Actually approve/merge (default is dry-run)")
    parser.add_argument("--once", action="store_true",
                        help="Run one cycle then exit")
    parser.add_argument("--pr", type=int,
                        help="Process a single PR number")
    parser.add_argument("--interval", type=int, default=300,
                        help="Poll interval in seconds (default: 300)")
    parser.add_argument("--owner", default="Azure")
    parser.add_argument("--repo", default="azure-sdk-for-net")
    parser.add_argument("--verbose", "-v", action="store_true")
    args = parser.parse_args()

    config = AgentConfig(
        owner=args.owner,
        repo=args.repo,
        dry_run=not args.live,
        poll_interval_seconds=args.interval,
        verbose=args.verbose,
    )

    _setup_logging(config)

    if args.pr:
        print(f"\n  Processing single PR #{args.pr}…\n")
        process_pr(config, args.pr)
        return

    if args.once:
        run_cycle(config)
        return

    # Continuous loop
    print("\n  Upgrade PR Agent starting (Ctrl+C to stop)\n")
    while _running:
        try:
            run_cycle(config)
        except Exception:
            log.exception("Unhandled error in cycle")

        if not _running:
            break

        log.info("Sleeping %d seconds...", config.poll_interval_seconds)
        for _ in range(config.poll_interval_seconds):
            if not _running:
                break
            time.sleep(1)

    print("\n  Agent stopped.\n")


def _setup_logging(config: AgentConfig):
    level = logging.DEBUG if config.verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] %(name)s: %(message)s"

    logging.basicConfig(level=level, format=fmt)

    # Also log to file
    fh = logging.FileHandler(config.log_file)
    fh.setLevel(logging.DEBUG)
    fh.setFormatter(logging.Formatter(fmt))
    logging.getLogger().addHandler(fh)


if __name__ == "__main__":
    main()
