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
# Install (one line, no checkout needed)
gh release download v1.0.0 -R JoshLove-msft/upgrade-pr-agent -p "*.nupkg" -D /tmp && dotnet tool install -g UpgradePrAgent --add-source /tmp && rm /tmp/UpgradePrAgent.1.0.0.nupkg

# Dry-run -- see what it would do (safe, no changes)
upgrade-pr-agent --once

# Process a single PR
upgrade-pr-agent --pr 57270

# Run continuously in dry-run mode
upgrade-pr-agent

# Go live -- actually approve & merge
upgrade-pr-agent --live
```

## Requirements

- .NET 10 SDK
- `gh` CLI authenticated (`gh auth login`)
- Git
- npm (for lockfile regeneration)

## Configuration

Use CLI flags:

| Flag | Default | Description |
|------|---------|-------------|
| `--live` | off | Enable approve/merge (default is dry-run) |
| `--once` | off | Run one cycle then exit |
| `--pr N` | -- | Process a single PR |
| `--interval N` | 300 | Seconds between poll cycles |
| `--owner` | Azure | GitHub org/owner |
| `--repo` | azure-sdk-for-net | Repository name |
| `-v` | off | Verbose logging |

## How it decides

```
PR Found
  -> CI pending?      -> Wait
  -> CI failing?      -> Log & skip (or fix if fixable issues detected)
  -> Review issues?   -> Attempt auto-fix -> push -> wait for CI
  -> CI green, no issues -> Approve -> Merge
  -> Superseded?      -> Comment & skip
```
