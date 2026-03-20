# Upgrade PR Agent

A background agent that monitors GitHub repos for automated dependency-upgrade
PRs (like TypeSpec generator bumps), fixes common issues, approves, and merges
them.

## What it does

1. **Discovers** open upgrade PRs from the `azure-sdk` bot
2. **Analyzes** CI status, review comments, and version consistency
3. **Fixes** common issues:
   - Stale `package-lock.json` files
   - Version mismatches between `package.json` and `.props` files
   - Missing lockfile regeneration
4. **Approves** PRs once CI is green and issues are resolved
5. **Merges** approved PRs (squash by default)
6. **Skips** PRs that are superseded by a newer version

## Quick start

```bash
# Dry-run — see what it would do (safe, no changes)
python agent.py --once

# Process a single PR
python agent.py --pr 57270

# Run continuously in dry-run mode
python agent.py

# Go live — actually approve & merge
python agent.py --live

# Customize poll interval (seconds)
python agent.py --live --interval 600
```

## Requirements

- Python 3.12+
- `gh` CLI authenticated (`gh auth login`)
- Git
- npm (for lockfile regeneration)

## Configuration

Edit `config.py` or use CLI flags:

| Flag | Default | Description |
|------|---------|-------------|
| `--live` | off | Enable approve/merge (default is dry-run) |
| `--once` | off | Run one cycle then exit |
| `--pr N` | — | Process a single PR |
| `--interval N` | 300 | Seconds between poll cycles |
| `--owner` | Azure | GitHub org/owner |
| `--repo` | azure-sdk-for-net | Repository name |
| `-v` | off | Verbose logging |

## How it decides

```
PR Found
  → CI pending?      → Wait
  → CI failing?      → Log & skip
  → Review issues?   → Attempt auto-fix → push → wait for CI
  → CI green, no issues → Approve → Merge
  → Superseded?      → Comment & skip
```

## Logs

All actions are logged to `upgrade-pr-agent.log` and stdout.
