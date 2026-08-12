<#
.SYNOPSIS
    Blocks tenant configuration, credentials and tenant PII from reaching git history.

.DESCRIPTION
    .gitignore is opt-out: it protects the files we thought of, and only while they keep their
    expected names. This is the belt to that pair of braces. It scans content — by default what is
    actually staged for commit — for the things that must never be committed from this repository:

      * access tokens, refresh tokens, JWTs and private keys
      * Azure OpenAI keys, shared demo passwords, Direct Line and client secrets
      * real demo-tenant identities: user principal names and tenant-specific resource names
      * files that should have been ignored (token caches, the journal, live config)

    Placeholders used in the example config and the docs are recognised and allowed, so a clean
    checkout scans clean.

    Exit code 0 means clean, 1 means something was found.

.PARAMETER Staged
    Scan what is staged for commit. This is the default and is what the pre-commit hook uses.

.PARAMETER All
    Scan every tracked file instead. Use for an audit.

.PARAMETER Path
    Scan specific files instead. Mostly useful for testing this script.

.EXAMPLE
    ./scripts/check-secrets.ps1

.EXAMPLE
    ./scripts/check-secrets.ps1 -All
#>

[CmdletBinding(DefaultParameterSetName = 'Staged')]
param(
    [Parameter(ParameterSetName = 'Staged')]
    [switch] $Staged,

    [Parameter(ParameterSetName = 'All')]
    [switch] $All,

    [Parameter(ParameterSetName = 'Path', Mandatory = $true)]
    [string[]] $Path
)

$ErrorActionPreference = 'Stop'

# Paths that must never be committed, whatever they contain.
$ForbiddenPaths = @(
    '(^|/)config/tenant-pulse\.json$'
    '(^|/)\.state/'
    '\.msalcache'
    '\.db$'
    '(^|/)\.env($|\.)'
    '\.storagestate\.json$'
    '(^|/)config/passwords\.json$'
    '\.(pfx|p12|pem|key)$'
)

# Content that must never be committed. Placeholders are excluded via -Allow.
$Rules = @(
    @{
        Name    = 'JWT or access token'
        Pattern = 'eyJ[A-Za-z0-9_\-]{15,}\.[A-Za-z0-9_\-]{15,}'
        Allow   = @()
    }
    @{
        Name    = 'Private key block'
        Pattern = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
        Allow   = @()
    }
    @{
        # "ApiKey": "abc...", SharedPassword, DirectLineSecret, secretText, client_secret
        Name    = 'Secret assigned a real value'
        Pattern = '"(ApiKey|SharedPassword|DirectLineSecret|secretText|client_secret|clientSecret)"\s*:\s*"[^"]{6,}"'
        Allow   = @('"\s*:\s*"<', '"\s*:\s*"\$\(', '"\s*:\s*"(null|REDACTED|CHANGEME|xxx+|\*+)"')
    }
    @{
        Name    = 'Secret in a query string or form body'
        Pattern = '[?&](client_secret|password|api-key|subscription-key)=[^\s&''"<]{8,}'
        Allow   = @('=\s*[''"]?<', '\$env:', '\$\(', '=\$')
    }
    @{
        # A literal string assigned to a password-ish name: password='…', "pwd": "…", pwd = "…".
        # Deliberately requires quotes, so code like `var password = Resolve(upn)` is not a hit.
        Name    = 'Password assigned a literal value'
        Pattern = '\b(client_secret|clientSecret|password|passwd|pwd|SharedPassword)\b\s*[:=]\s*[''"][^''"<$]{8,}[''"]'
        Allow   = @('[''"]<', '\$env:', '\$\(', '\$\{', '[''"](null|REDACTED|CHANGEME|placeholder|xxx+|\*+)[''"]')
    }
    @{
        # Demo tenants are M365CPI<digits> / M365x<digits>. All-zero forms are doc placeholders.
        Name    = 'Real demo-tenant user principal name or domain'
        Pattern = '\b[A-Za-z0-9._%+-]*@?M365(CPI|x)\d{4,}\.onmicrosoft\.com'
        Allow   = @('M365(CPI|x)0+\.onmicrosoft\.com')
    }
    @{
        Name    = 'Azure OpenAI resource endpoint'
        Pattern = 'https://[a-z0-9][a-z0-9-]{2,}\.openai\.azure\.com'
        Allow   = @('your-resource', '<', 'my-resource', 'example')
    }
    @{
        Name    = 'Azure OpenAI style API key'
        Pattern = '\b[A-Za-z0-9]{60,}\b'
        Allow   = @('sha256|integrity|[A-Za-z0-9+/]{20,}={1,2}')
    }
)

function Get-Targets {
    switch ($PSCmdlet.ParameterSetName) {
        'All' { return @(git ls-files) }
        'Path' { return $Path }
        default { return @(git diff --cached --name-only --diff-filter=ACMR) }
    }
}

function Get-Content-ForScan {
    param([string] $File)

    if ($PSCmdlet.ParameterSetName -eq 'Staged') {
        # Read the staged blob, not the working copy: they can differ.
        $text = git show ":$File" 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return $text
    }

    if (-not (Test-Path $File -PathType Leaf)) { return $null }
    return Get-Content $File -ErrorAction SilentlyContinue
}

$targets = @(Get-Targets | Where-Object { $_ })
$findings = @()

foreach ($file in $targets) {
    foreach ($forbidden in $ForbiddenPaths) {
        if ($file -match $forbidden) {
            $findings += [pscustomobject]@{
                File = $file; Line = 0; Rule = 'Forbidden path'; Text = $file
            }
        }
    }

    # Don't scan this script: it necessarily contains the patterns it looks for.
    if ($file -match 'scripts/check-secrets\.ps1$') { continue }

    $lines = Get-Content-ForScan -File $file
    if (-not $lines) { continue }

    $n = 0
    foreach ($line in $lines) {
        $n++
        if ($line.Length -gt 4000) { continue }

        foreach ($rule in $Rules) {
            if ($line -notmatch $rule.Pattern) { continue }

            $allowed = $false
            foreach ($allow in $rule.Allow) {
                if ($allow -and $line -match $allow) { $allowed = $true; break }
            }
            if ($allowed) { continue }

            $snippet = $line.Trim()
            if ($snippet.Length -gt 100) { $snippet = $snippet.Substring(0, 100) + '…' }

            $findings += [pscustomobject]@{
                File = $file; Line = $n; Rule = $rule.Name; Text = $snippet
            }
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Host "check-secrets: clean ($($targets.Count) file(s) scanned)." -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host 'check-secrets: refusing to let this be committed' -ForegroundColor Red
Write-Host ('-' * 70)
foreach ($f in $findings) {
    $where = if ($f.Line -gt 0) { "$($f.File):$($f.Line)" } else { $f.File }
    Write-Host ("  {0}" -f $f.Rule) -ForegroundColor Yellow
    Write-Host ("    $where") -ForegroundColor DarkGray
    Write-Host ("    $($f.Text)")
}
Write-Host ('-' * 70)
Write-Host 'Unstage it (git restore --staged <file>) and keep it in config/ or .state/, which are gitignored.'
Write-Host 'If this is a false positive, adjust the Allow list in scripts/check-secrets.ps1.'
Write-Host ''
exit 1
