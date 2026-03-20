"""Fix common issues in upgrade PRs by cloning, patching, and pushing."""

import json
import logging
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

import gh
from analyzer import FixableIssue, PRAnalysis
from config import AgentConfig

log = logging.getLogger(__name__)


class PRFixer:
    def __init__(self, config: AgentConfig):
        self.config = config
        self.work_dir: Path | None = None

    def fix_issues(self, analysis: PRAnalysis) -> bool:
        """Attempt to fix all fixable issues. Returns True if all fixed."""
        if not analysis.fixable_issues:
            return True

        log.info("Attempting to fix %d issues in PR #%d",
                 len(analysis.fixable_issues), analysis.number)

        if self.config.dry_run:
            for issue in analysis.fixable_issues:
                log.info("[DRY RUN] Would fix: %s (%s)",
                         issue.description, issue.fix_type)
            return True

        try:
            self._setup_workspace(analysis)
            fixed_count = 0

            for issue in analysis.fixable_issues:
                if self._fix_issue(issue):
                    fixed_count += 1
                else:
                    log.warning("Could not fix: %s", issue.description)

            if fixed_count > 0:
                self._commit_and_push(analysis, fixed_count)

            return fixed_count == len(analysis.fixable_issues)

        except Exception:
            log.exception("Error fixing issues in PR #%d", analysis.number)
            return False
        finally:
            self._cleanup()

    def _setup_workspace(self, analysis: PRAnalysis):
        """Clone the repo and checkout the PR branch."""
        if self.config.workspace_dir:
            self.work_dir = Path(self.config.workspace_dir) / f"pr-{analysis.number}"
        else:
            self.work_dir = Path(tempfile.mkdtemp(prefix="upgrade-pr-"))

        repo_url = f"https://github.com/{self.config.owner}/{self.config.repo}.git"

        log.info("Cloning %s (branch %s) to %s",
                 repo_url, analysis.head_ref, self.work_dir)

        # Shallow clone just the PR branch
        subprocess.run([
            "git", "clone", "--depth", "1",
            "--branch", analysis.head_ref,
            "--single-branch", repo_url,
            str(self.work_dir)
        ], check=True, capture_output=True, text=True, timeout=300)

    def _fix_issue(self, issue: FixableIssue) -> bool:
        """Fix a single issue. Returns True if fixed."""
        handlers = {
            "lockfile_stale": self._fix_lockfile,
            "version_mismatch": self._fix_version_mismatch,
            "regen_needed": self._fix_regen,
        }
        handler = handlers.get(issue.fix_type)
        if not handler:
            log.warning("No handler for fix type: %s", issue.fix_type)
            return False
        return handler(issue)

    def _fix_lockfile(self, issue: FixableIssue) -> bool:
        """Regenerate package-lock.json by running npm install."""
        pkg_dir = self.work_dir

        # Determine which package.json directory to run npm install in
        if "http-client-csharp" in issue.file_path:
            pkg_dir = self.work_dir / "eng" / "packages" / "http-client-csharp"
        elif "emitter-package" in issue.file_path:
            # The emitter package files are at the eng/ level
            pkg_dir = self.work_dir / "eng"

        pkg_json = pkg_dir / "package.json"
        if not pkg_json.exists():
            log.error("package.json not found at %s", pkg_json)
            return False

        log.info("Running npm install in %s", pkg_dir)
        result = subprocess.run(
            ["npm", "install", "--package-lock-only"],
            cwd=str(pkg_dir),
            capture_output=True, text=True, timeout=120,
        )

        if result.returncode != 0:
            log.error("npm install failed: %s", result.stderr)
            return False

        log.info("Successfully regenerated lockfile")
        return True

    def _fix_version_mismatch(self, issue: FixableIssue) -> bool:
        """Fix a version mismatch by updating the specified file."""
        file_path = self.work_dir / issue.file_path
        if not file_path.exists():
            log.error("File not found: %s", file_path)
            return False

        content = file_path.read_text(encoding="utf-8")

        # Try to extract what version should be there from the PR's other files
        # Look at the main version props file for the source of truth
        props_file = (self.work_dir / "eng" / "centralpackagemanagement"
                      / "Directory.Generation.Packages.props")

        if props_file.exists():
            props = props_file.read_text(encoding="utf-8")
            # Extract UnbrandedGeneratorVersion
            m = re.search(
                r'<UnbrandedGeneratorVersion>(.*?)</UnbrandedGeneratorVersion>',
                props
            )
            target_version = m.group(1) if m else None

            if target_version and "http-client-csharp" in issue.file_path:
                # Update @typespec/http-client-csharp version
                old_pattern = (
                    r'"@typespec/http-client-csharp":\s*"[^"]*"'
                )
                new_value = (
                    f'"@typespec/http-client-csharp": "{target_version}"'
                )
                new_content = re.sub(old_pattern, new_value, content)

                if new_content != content:
                    file_path.write_text(new_content, encoding="utf-8")
                    log.info("Updated version in %s to %s",
                             issue.file_path, target_version)
                    return True

        # If we have a suggested version from the comment
        suggested = issue.details.get("suggested_version")
        if suggested:
            log.info("Suggested version fix available: %s", suggested)
            # Attempt to apply but be conservative
            return False

        log.warning("Could not determine correct version for %s",
                    issue.file_path)
        return False

    def _fix_regen(self, issue: FixableIssue) -> bool:
        """Run code regeneration scripts."""
        script = (self.work_dir / "eng" / "packages" / "http-client-csharp"
                  / "eng" / "scripts" / "Generate.ps1")
        if not script.exists():
            log.error("Generate.ps1 not found")
            return False

        log.info("Running code regeneration...")
        result = subprocess.run(
            ["pwsh", "-File", str(script)],
            cwd=str(self.work_dir),
            capture_output=True, text=True, timeout=600,
        )

        if result.returncode != 0:
            log.error("Regeneration failed: %s", result.stderr[:500])
            return False

        return True

    def _commit_and_push(self, analysis: PRAnalysis, fix_count: int):
        """Commit the fixes and push to the PR branch."""
        cwd = str(self.work_dir)

        subprocess.run(
            ["git", "add", "-A"], cwd=cwd, check=True,
            capture_output=True, text=True,
        )

        # Check if there are actual changes
        status = subprocess.run(
            ["git", "status", "--porcelain"], cwd=cwd,
            capture_output=True, text=True,
        )
        if not status.stdout.strip():
            log.info("No actual changes to commit")
            return

        commit_msg = (
            f"Fix {fix_count} issue(s) in upgrade PR\n\n"
            "Automated fixes applied by upgrade-pr-agent:\n"
        )
        for issue in analysis.fixable_issues:
            commit_msg += f"- {issue.description}\n"
        commit_msg += (
            "\nCo-authored-by: Copilot "
            "<223556219+Copilot@users.noreply.github.com>"
        )

        subprocess.run(
            ["git", "commit", "-m", commit_msg], cwd=cwd,
            check=True, capture_output=True, text=True,
        )
        subprocess.run(
            ["git", "push"], cwd=cwd,
            check=True, capture_output=True, text=True, timeout=120,
        )
        log.info("Pushed fixes to branch %s", analysis.head_ref)

    def _cleanup(self):
        """Remove temporary workspace."""
        if self.work_dir and self.work_dir.exists():
            if not self.config.workspace_dir:
                shutil.rmtree(self.work_dir, ignore_errors=True)
            self.work_dir = None
