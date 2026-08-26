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
    'docs/distribution/public-release.md',
    'tools/whisper-uat/fixtures/manifest.json'
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

    $fixtureManifestPath = Join-Path $repoRoot 'tools/whisper-uat/fixtures/manifest.json'
    $fixtureManifest = Get-Content -LiteralPath $fixtureManifestPath -Raw | ConvertFrom-Json
    if ($fixtureManifest.dataset -cne 'PolyAI/minds14' -or
        $fixtureManifest.datasetRevision -cne '40ce77cb32a384e4d50a568e1ec39ac804019d33' -or
        $fixtureManifest.license -cne 'CC-BY-4.0' -or
        $fixtureManifest.source -cne 'https://huggingface.co/datasets/PolyAI/minds14' -or
        @($fixtureManifest.fixtures).Count -ne 12) {
        throw 'The reviewed public Whisper fixture manifest has unapproved provenance.'
    }

    $expectedFixtureRows = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    @(
        'en-US/train/0',
        'fr-FR/train/0',
        'de-DE/train/0',
        'de-DE/train/100',
        'de-DE/train/200',
        'de-DE/train/300',
        'de-DE/train/400',
        'es-ES/train/0',
        'es-ES/train/100',
        'es-ES/train/200',
        'es-ES/train/300',
        'es-ES/train/400'
    ) | ForEach-Object { [void]$expectedFixtureRows.Add($_) }
    $reviewedFixturePaths = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $reviewedFixtureRows = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($fixture in @($fixtureManifest.fixtures)) {
        $expectedFile = "$($fixture.config)-row$($fixture.row).wav"
        $rowKey = "$($fixture.config)/$($fixture.split)/$($fixture.row)"
        if ([string]::IsNullOrWhiteSpace($fixture.file) -or
            $fixture.file -cne $expectedFile -or
            [IO.Path]::GetFileName($fixture.file) -cne $fixture.file -or
            $fixture.config -notmatch '^[a-z]{2}-[A-Z]{2}$' -or
            $fixture.split -cne 'train' -or
            $fixture.row -lt 0 -or
            [string]::IsNullOrWhiteSpace($fixture.transcription) -or
            $fixture.sha256 -notmatch '^[a-f0-9]{64}$' -or
            -not $expectedFixtureRows.Contains($rowKey) -or
            -not $reviewedFixtureRows.Add($rowKey)) {
            throw "The reviewed public fixture manifest contains an invalid row: $rowKey"
        }

        if ($rowKey -ceq 'en-US/train/0') {
            if ($fixture.evaluationTranscription -cne
                    'I would like to set up a joint account with my partner. How do I proceed with doing that?' -or
                $fixture.referenceStatus -cne
                    'source-reference-ends-at-7s-two-engine-timestamp-review') {
                throw 'The reviewed English evaluation reference has drifted.'
            }
        }
        elseif (-not [string]::IsNullOrWhiteSpace($fixture.evaluationTranscription) -or
                -not [string]::IsNullOrWhiteSpace($fixture.referenceStatus)) {
            throw "An unreviewed evaluation reference was added: $rowKey"
        }

        $relativeFixture = "tools/whisper-uat/fixtures/$($fixture.file)"
        $fixturePath = Join-Path $repoRoot $relativeFixture
        if (-not $reviewedFixturePaths.Add($relativeFixture) -or
            $tracked -cnotcontains $relativeFixture -or
            -not (Test-Path -LiteralPath $fixturePath -PathType Leaf)) {
            throw "A reviewed public fixture is missing, duplicated, or untracked: $relativeFixture"
        }

        $fixtureFile = Get-Item -LiteralPath $fixturePath
        if ($fixtureFile.Length -le 0 -or $fixtureFile.Length -gt 1MB) {
            throw "A reviewed public fixture has an unsafe size: $relativeFixture"
        }
        $actualHash = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -cne $fixture.sha256) {
            throw "A reviewed public fixture hash does not match its manifest: $relativeFixture"
        }
    }

    if (-not $reviewedFixtureRows.SetEquals($expectedFixtureRows)) {
        throw 'The reviewed public fixture manifest does not contain the exact approved row set.'
    }

    $trackedFixtureAudio = @($tracked | Where-Object {
        $_.StartsWith('tools/whisper-uat/fixtures/', [StringComparison]::Ordinal) -and
        [IO.Path]::GetExtension($_) -ieq '.wav'
    })
    $unreviewedFixtureAudio = @($trackedFixtureAudio | Where-Object {
        -not $reviewedFixturePaths.Contains($_)
    })
    if ($unreviewedFixtureAudio.Count -gt 0 -or
        $trackedFixtureAudio.Count -ne $reviewedFixturePaths.Count) {
        throw "Unreviewed public fixture audio is tracked: $($unreviewedFixtureAudio -join ', ')"
    }

    $forbiddenExtensions = @('.gguf', '.onnx', '.ort', '.wav', '.mp3', '.pfx', '.p12', '.pem', '.key')
    $forbiddenTracked = @($tracked | Where-Object {
        $extension = [IO.Path]::GetExtension($_).ToLowerInvariant()
        $isReviewedPublicFixture = $extension -eq '.wav' -and $reviewedFixturePaths.Contains($_)
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

    Write-Host "Public-release repository audit passed: $($tracked.Count) tracked files, $($reviewedFixturePaths.Count) reviewed public audio fixtures, no private paths or secret-shaped content."
}
finally {
    Pop-Location
}
