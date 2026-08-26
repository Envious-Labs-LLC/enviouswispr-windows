param(
    [switch]$VerifyGitHubSecurity
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$requiredFiles = @(
    'LICENSE',
    'PRIVACY.md',
    'README.md',
    'SECURITY.md',
    'SUPPORT.md',
    'THIRD-PARTY-NOTICES.md',
    'docs/distribution/artifact-license-inventory.json',
    'docs/distribution/artifact-license-inventory.md',
    'docs/distribution/public-release.md'
)
foreach ($relative in $requiredFiles) {
    $path = Join-Path $repoRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Item -LiteralPath $path).Length -lt 100) {
        throw "Required public-release artifact is missing or empty: $relative"
    }
}

$license = Get-Content -LiteralPath (Join-Path $repoRoot 'LICENSE') -Raw
if (-not $license.Contains('GNU GENERAL PUBLIC LICENSE') -or
    -not $license.Contains('Version 3, 29 June 2007') -or
    -not $license.Contains('END OF TERMS AND CONDITIONS')) {
    throw 'LICENSE is not the complete GNU GPL version 3 text.'
}

Push-Location $repoRoot
try {
    & pwsh -NoProfile -File '.\scripts\generate-third-party-notices.ps1' -Verify
    if ($LASTEXITCODE -ne 0) {
        throw 'Production third-party notice verification failed.'
    }

    & pwsh -NoProfile -File '.\scripts\validate-artifact-license-inventory.ps1'
    if ($LASTEXITCODE -ne 0) {
        throw 'Model/native artifact license inventory verification failed.'
    }

    $tracked = @(git ls-files)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) {
        throw 'Tracked-file inventory failed.'
    }

    $forbiddenExtensions = @('.gguf', '.onnx', '.ort', '.wav', '.mp3', '.pfx', '.p12', '.pem', '.key')
    $forbiddenTracked = @($tracked | Where-Object {
        $extension = [IO.Path]::GetExtension($_).ToLowerInvariant()
        $isReviewedPublicFixture = $extension -eq '.wav' -and
            $_.StartsWith('tools/whisper-uat/fixtures/', [StringComparison]::Ordinal)
        $forbiddenExtensions -contains $extension -and -not $isReviewedPublicFixture
    })
    if ($forbiddenTracked.Count -gt 0) {
        throw "Forbidden model/audio/key material is tracked: $($forbiddenTracked -join ', ')"
    }

    # Assemble the Unix prefix so this audit does not flag its own detection rule.
    $unixHomePrefix = '/ho' + 'me/'
    $privatePathPattern = '(?i)([A-Z]:(?:\\{1,2}|/)Users(?:\\{1,2}|/)(?!<|%USERNAME%|%USERPROFILE%)|' +
        [regex]::Escape($unixHomePrefix) + '(?!<))'
    $secretPattern = '(?i)(sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|client_secret\s*[:=]\s*[^\s''"]+)'
    $violations = [System.Collections.Generic.List[string]]::new()
    foreach ($relative in $tracked) {
        $path = Join-Path $repoRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }
        try {
            $text = Get-Content -LiteralPath $path -Raw -ErrorAction Stop
        }
        catch {
            continue
        }
        if ($text -match $privatePathPattern) {
            $violations.Add("private machine path: $relative")
        }
        if ($text -match $secretPattern) {
            $violations.Add("secret-shaped content: $relative")
        }
    }
    if ($violations.Count -gt 0) {
        throw "Public repository scan failed:`n$($violations -join [Environment]::NewLine)"
    }

    if ($VerifyGitHubSecurity) {
        if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
            throw 'GitHub CLI is required for -VerifyGitHubSecurity.'
        }
        $repository = gh api repos/Envious-Labs-LLC/enviouswispr-windows | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) {
            throw 'GitHub repository security state could not be read.'
        }
        $security = $repository.security_and_analysis
        foreach ($setting in @(
            'dependabot_security_updates',
            'secret_scanning',
            'secret_scanning_push_protection',
            'secret_scanning_validity_checks')) {
            if ($security.$setting.status -ne 'enabled') {
                throw "GitHub security setting is not enabled: $setting"
            }
        }
        $privateReporting = gh api `
            repos/Envious-Labs-LLC/enviouswispr-windows/private-vulnerability-reporting |
            ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $privateReporting.enabled -ne $true) {
            throw 'GitHub private vulnerability reporting is not enabled.'
        }
    }

    Write-Host "Public-release repository audit passed: $($tracked.Count) tracked files, no private paths or secret-shaped content."
}
finally {
    Pop-Location
}
