<#
.SYNOPSIS
    Enables the repository's git hooks, so secrets and tenant PII cannot be committed by accident.

.DESCRIPTION
    Points git at the tracked .githooks folder instead of .git/hooks. Because the hooks are version
    controlled, everyone who runs this gets the same protection, and it survives a fresh clone.

    The pre-commit hook runs scripts/check-secrets.ps1 against the staged changes and fails the
    commit if it finds tokens, keys, passwords or real demo-tenant identities.

.PARAMETER Remove
    Restore git's default hooks path.

.EXAMPLE
    ./scripts/install-git-hooks.ps1
#>

[CmdletBinding()]
param(
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
Push-Location $repoRoot

try {
    if ($Remove) {
        git config --unset core.hooksPath
        Write-Host 'Restored the default git hooks path.' -ForegroundColor Yellow
        return
    }

    git config core.hooksPath .githooks

    # Matters on macOS/Linux and for WSL; harmless on Windows.
    $hook = Join-Path $repoRoot '.githooks/pre-commit'
    if ($IsLinux -or $IsMacOS) {
        chmod +x $hook
    }
    git update-index --chmod=+x .githooks/pre-commit 2>$null | Out-Null

    Write-Host ''
    Write-Host 'Git hooks enabled.' -ForegroundColor Green
    Write-Host "  core.hooksPath = $(git config core.hooksPath)"
    Write-Host ''
    Write-Host 'Every commit is now scanned for:' -ForegroundColor White
    Write-Host '  - access tokens, refresh tokens, JWTs and private keys'
    Write-Host '  - Azure OpenAI keys, shared demo passwords, Direct Line and client secrets'
    Write-Host '  - real demo-tenant user principal names and resource endpoints'
    Write-Host '  - files that should have stayed ignored (token caches, journal, live config)'
    Write-Host ''
    Write-Host 'Audit the whole tree at any time with:' -ForegroundColor White
    Write-Host '  ./scripts/check-secrets.ps1 -All'
    Write-Host ''
}
finally {
    Pop-Location
}
