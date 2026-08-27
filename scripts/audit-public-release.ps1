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
    'src/Production/EnviousWispr.Audio/Assets/RecordingSounds/manifest.json',
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
    $reviewedEvaluationReferences =
        [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $reviewedEvaluationReferences.Add(
        'en-US/train/0',
        [pscustomobject]@{
            Evaluation = 'I would like to set up a joint account with my partner. How do I proceed with doing that?'
            Status = 'source-reference-ends-at-7s-two-engine-timestamp-review'
        })
    $reviewedEvaluationReferences.Add(
        'de-DE/train/100',
        [pscustomobject]@{
            Evaluation = 'Guten Tag, ich möchte eine Lastschrift aufgeben und hätte diesbezüglich noch ein paar Fragen. Zum Beispiel wüsste ich gerne, wie lange es ungefähr dauert, bis mein Geld bei dem Empfänger ankommt. Und ich würde auch gerne wissen, ob da irgendwelche Gebühren anfallen für mich oder für den Empfänger.'
            Status = 'source-reference-truncated-at-11.6s-two-pack-two-mode-timestamp-review'
        })
    $reviewedEvaluationReferences.Add(
        'es-ES/train/0',
        [pscustomobject]@{
            Evaluation = 'Hola, buenas. A ver, tengo un problema con vuestra aplicación. Resulta que quiero hacer una transferencia bancaria a una cuenta conocida, pero me da error la aplicación. A ver qué puede ser.'
            Status = 'source-reference-truncated-at-10.1s-two-pack-two-mode-timestamp-review'
        })
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

        $reviewedEvaluationReference = $null
        if ($reviewedEvaluationReferences.TryGetValue(
                $rowKey,
                [ref]$reviewedEvaluationReference)) {
            if ($fixture.evaluationTranscription -cne $reviewedEvaluationReference.Evaluation -or
                $fixture.referenceStatus -cne $reviewedEvaluationReference.Status) {
                throw "A reviewed evaluation reference has drifted: $rowKey"
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

    $recordingSoundRoot = 'src/Production/EnviousWispr.Audio/Assets/RecordingSounds'
    $recordingSoundManifestPath = Join-Path $repoRoot "$recordingSoundRoot/manifest.json"
    $recordingSoundManifest = Get-Content -LiteralPath $recordingSoundManifestPath -Raw |
        ConvertFrom-Json
    if ($recordingSoundManifest.schemaVersion -ne 1 -or
        $recordingSoundManifest.owner -cne 'Envious Labs LLC' -or
        $recordingSoundManifest.purpose -cne 'Original EnviousWispr recording start and stop confirmations' -or
        $recordingSoundManifest.sourceRepository -cne 'https://github.com/saurabhav88/EnviousWispr' -or
        $recordingSoundManifest.sourceRevision -cne '5dab8db8abc593dd0c60a06ad877a6304d51563b' -or
        @($recordingSoundManifest.assets).Count -ne 24) {
        throw 'The first-party recording sound manifest has unapproved provenance.'
    }

    $expectedSoundFiles = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $soundStems = @(
        'airGlint', 'cloudPop', 'dustMote', 'lowNod', 'mutedConfirm', 'paperTap',
        'roundPebble', 'satinShift', 'softHush', 'velvetHush', 'velvetTap', 'whisperTick')
    foreach ($stem in $soundStems) {
        [void]$expectedSoundFiles.Add("${stem}_start.wav")
        [void]$expectedSoundFiles.Add("${stem}_stop.wav")
    }
    $reviewedSoundPaths = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $reviewedSoundFiles = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($asset in @($recordingSoundManifest.assets)) {
        if ([string]::IsNullOrWhiteSpace($asset.file) -or
            [IO.Path]::GetFileName($asset.file) -cne $asset.file -or
            -not $expectedSoundFiles.Contains($asset.file) -or
            -not $reviewedSoundFiles.Add($asset.file) -or
            $asset.sha256 -notmatch '^[a-f0-9]{64}$') {
            throw "The recording sound manifest contains an invalid asset: $($asset.file)"
        }

        $relativeSound = "$recordingSoundRoot/$($asset.file)"
        $soundPath = Join-Path $repoRoot $relativeSound
        if (-not $reviewedSoundPaths.Add($relativeSound) -or
            $tracked -cnotcontains $relativeSound -or
            -not (Test-Path -LiteralPath $soundPath -PathType Leaf)) {
            throw "A reviewed recording sound is missing, duplicated, or untracked: $relativeSound"
        }
        $soundFile = Get-Item -LiteralPath $soundPath
        if ($soundFile.Length -le 0 -or $soundFile.Length -gt 100KB) {
            throw "A reviewed recording sound has an unsafe size: $relativeSound"
        }
        $actualHash = (Get-FileHash -LiteralPath $soundPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -cne $asset.sha256) {
            throw "A reviewed recording sound hash does not match its manifest: $relativeSound"
        }
    }
    if (-not $reviewedSoundFiles.SetEquals($expectedSoundFiles)) {
        throw 'The recording sound manifest does not contain the exact approved asset set.'
    }

    $forbiddenExtensions = @('.gguf', '.onnx', '.ort', '.wav', '.mp3', '.pfx', '.p12', '.pem', '.key')
    $forbiddenTracked = @($tracked | Where-Object {
        $extension = [IO.Path]::GetExtension($_).ToLowerInvariant()
        $isReviewedAudio = $extension -eq '.wav' -and
            ($reviewedFixturePaths.Contains($_) -or $reviewedSoundPaths.Contains($_))
        $forbiddenExtensions -contains $extension -and -not $isReviewedAudio
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
