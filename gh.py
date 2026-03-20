"""Thin wrapper around the `gh` CLI for GitHub API calls."""

import json
import logging
import subprocess
from typing import Any

log = logging.getLogger(__name__)


def _run(args: list[str], check: bool = True) -> str:
    """Run a gh CLI command and return stdout."""
    cmd = ["gh"] + args
    log.debug("Running: %s", " ".join(cmd))
    result = subprocess.run(
        cmd, capture_output=True, text=True, check=check, timeout=120,
        encoding="utf-8", errors="replace",
    )
    if result.returncode != 0:
        log.error("gh stderr: %s", result.stderr.strip())
    return result.stdout.strip()


def api(endpoint: str, method: str = "GET", fields: dict | None = None,
        jq_filter: str | None = None, paginate: bool = False) -> Any:
    """Call the GitHub REST API via `gh api`."""
    args = ["api", endpoint, "--method", method]
    if jq_filter:
        args += ["--jq", jq_filter]
    if paginate:
        args.append("--paginate")
    for k, v in (fields or {}).items():
        args += ["-f", f"{k}={v}"]
    raw = _run(args)
    if not raw:
        return None
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return raw


def list_open_prs(owner: str, repo: str, author: str,
                  search_query: str = "") -> list[dict]:
    """Search for open PRs matching criteria using gh search flags."""
    args = [
        "search", "prs",
        "--repo", f"{owner}/{repo}",
        "--author", author,
        "--state", "open",
        "--json", "number,title,state,labels,createdAt",
        "--limit", "20",
        "--", search_query,
    ]
    raw = _run(args, check=False)
    if not raw:
        return []
    return json.loads(raw)


def get_pr(owner: str, repo: str, number: int) -> dict:
    return api(f"/repos/{owner}/{repo}/pulls/{number}")


def get_pr_files(owner: str, repo: str, number: int) -> list[dict]:
    return api(f"/repos/{owner}/{repo}/pulls/{number}/files", paginate=True)


def get_pr_reviews(owner: str, repo: str, number: int) -> list[dict]:
    return api(f"/repos/{owner}/{repo}/pulls/{number}/reviews", paginate=True)


def get_pr_review_comments(owner: str, repo: str, number: int) -> list[dict]:
    return api(
        f"/repos/{owner}/{repo}/pulls/{number}/comments", paginate=True
    )


def get_check_runs(owner: str, repo: str, sha: str) -> list[dict]:
    data = api(f"/repos/{owner}/{repo}/commits/{sha}/check-runs",
               jq_filter=".check_runs")
    return data or []


def get_combined_status(owner: str, repo: str, sha: str) -> dict:
    return api(f"/repos/{owner}/{repo}/commits/{sha}/status")


def approve_pr(owner: str, repo: str, number: int, body: str = "") -> dict:
    """Submit an approving review."""
    args = ["pr", "review", str(number), "--approve",
            "--repo", f"{owner}/{repo}"]
    if body:
        args += ["--body", body]
    return _run(args)


def merge_pr(owner: str, repo: str, number: int,
             method: str = "squash") -> str:
    args = ["pr", "merge", str(number), f"--{method}",
            "--repo", f"{owner}/{repo}", "--auto"]
    return _run(args, check=False)


def add_comment(owner: str, repo: str, number: int, body: str) -> dict:
    return api(
        f"/repos/{owner}/{repo}/issues/{number}/comments",
        method="POST",
        fields={"body": body},
    )


def get_authenticated_user() -> str:
    return _run(["auth", "status", "--active", "-t"],
                check=False).split("\n")[0]
