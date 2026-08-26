param(
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$noticePath = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md'

function Resolve-DotNet10 {
    $candidates = @(
        (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dotnet\dotnet.exe'),
        (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    foreach ($candidate in $candidates) {
        if ((Test-Path -LiteralPath $candidate -PathType Leaf) -and
            ((& $candidate --list-sdks) -match '^10\.')) {
            return $candidate
        }
    }

    throw '.NET 10 SDK is required to generate production package notices.'
}

function MarkdownCell([string]$value) {
    return $value.Replace('|', '\|').Replace("`r", '').Replace("`n", ' ')
}

$dotnet = Resolve-DotNet10
$projects = @(
    'src/Production/EnviousWispr.App/EnviousWispr.App.csproj',
    'src/Production/EnviousWispr.RuntimeWorker/EnviousWispr.RuntimeWorker.csproj'
)
$resolved = [System.Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::OrdinalIgnoreCase)

Push-Location $repoRoot
try {
    foreach ($project in $projects) {
        $json = @(& $dotnet list $project package --include-transitive --format json) -join [Environment]::NewLine
        if ($LASTEXITCODE -ne 0) {
            throw "Package graph resolution failed for $project."
        }

        $graph = $json | ConvertFrom-Json -ErrorAction Stop
        foreach ($projectGraph in $graph.projects) {
            foreach ($framework in $projectGraph.frameworks) {
                foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                    if ([string]::IsNullOrWhiteSpace($package.id) -or
                        [string]::IsNullOrWhiteSpace($package.resolvedVersion)) {
                        continue
                    }

                    $key = "$($package.id)|$($package.resolvedVersion)"
                    $resolved[$key] = [pscustomobject]@{
                        Id = [string]$package.id
                        Version = [string]$package.resolvedVersion
                    }
                }
            }
        }
    }

    $globalPackages = ((& $dotnet nuget locals global-packages --list) `
        -replace '^global-packages: ', '').Trim()
    if (-not (Test-Path -LiteralPath $globalPackages -PathType Container)) {
        throw 'The NuGet global package cache could not be located.'
    }

    $allowedLegacyUrls = @('https://aka.ms/WinSDKLicenseURL')
    $rows = foreach ($package in ($resolved.Values | Sort-Object Id, Version)) {
        $packageDirectory = Join-Path `
            (Join-Path $globalPackages $package.Id.ToLowerInvariant()) `
            $package.Version.ToLowerInvariant()
        $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File |
            Select-Object -First 1
        if ($null -eq $nuspec) {
            throw "NuGet metadata is missing for $($package.Id) $($package.Version)."
        }

        [xml]$xml = Get-Content -LiteralPath $nuspec.FullName -Raw
        $metadata = $xml.package.metadata
        $licenseNode = $metadata.SelectSingleNode('*[local-name()="license"]')
        $kind = if ($licenseNode) { $licenseNode.GetAttribute('type') } else { 'url' }
        $value = if ($licenseNode) { $licenseNode.InnerText.Trim() } else { [string]$metadata.licenseUrl }
        $hash = 'not-applicable'

        switch ($kind) {
            'expression' {
                if ($value -notmatch '^[A-Za-z0-9.+() -]{1,120}$') {
                    throw "Unsafe or empty SPDX expression for $($package.Id)."
                }
            }
            'file' {
                $licensePath = [IO.Path]::GetFullPath((Join-Path $packageDirectory $value))
                $expectedRoot = [IO.Path]::TrimEndingDirectorySeparator(
                    [IO.Path]::GetFullPath($packageDirectory)) + [IO.Path]::DirectorySeparatorChar
                if (-not $licensePath.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase) -or
                    -not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
                    throw "Embedded license file is missing or unsafe for $($package.Id)."
                }
                $hash = (Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash
                $value = "embedded package file $value"
            }
            'url' {
                if ($value -notin $allowedLegacyUrls) {
                    throw "Unreviewed legacy license URL for $($package.Id): $value"
                }
            }
            default {
                throw "Unsupported license metadata type for $($package.Id): $kind"
            }
        }

        [pscustomobject]@{
            Id = $package.Id
            Version = $package.Version
            Kind = $kind
            License = $value
            Sha256 = $hash
        }
    }

    $criticalPackages = @(
        'Microsoft.ML.OnnxRuntime.Gpu',
        'Microsoft.WindowsAppSDK',
        'NAudio.Wasapi',
        'Velopack',
        'Whisper.net',
        'Whisper.net.Runtime',
        'Whisper.net.Runtime.Cuda.Windows'
    )
    foreach ($critical in $criticalPackages) {
        if ($rows.Id -notcontains $critical) {
            throw "Critical production dependency is absent from the notice graph: $critical"
        }
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('# Third-party notices')
    $lines.Add('')
    $lines.Add('This inventory is generated from the exact resolved NuGet graphs of the production WinUI app and')
    $lines.Add('runtime worker. Run `scripts/generate-third-party-notices.ps1` after dependency changes and commit')
    $lines.Add('the result. Canonical validation rejects missing license metadata or notice drift.')
    $lines.Add('')
    $lines.Add('| Package | Version | License metadata | License-file SHA-256 |')
    $lines.Add('| --- | --- | --- | --- |')
    foreach ($row in $rows) {
        $lines.Add("| $(MarkdownCell $row.Id) | $(MarkdownCell $row.Version) | " +
            "$(MarkdownCell "$($row.Kind): $($row.License)") | $(MarkdownCell $row.Sha256) |")
    }
    $lines.Add('')
    $lines.Add('Model packs, CUDA redistributables, and EG-1 are separately delivered artifacts. Their signed model')
    $lines.Add('manifests must carry the exact upstream license notice and acceptance requirements; this NuGet')
    $lines.Add('inventory does not approve or replace those notices. A public release remains blocked until every')
    $lines.Add('shipped artifact has a reviewed license record. Source evidence and open decisions are tracked in')
    $lines.Add('`docs/distribution/artifact-license-inventory.md`.')
    # Keep generated Markdown stable across Windows and Linux validation hosts.
    $content = ($lines -join "`n") + "`n"

    if ($Verify) {
        if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
            throw 'THIRD-PARTY-NOTICES.md is missing.'
        }
        # Git may materialize the tracked Markdown with CRLF on Windows even though
        # the generated contract is intentionally LF-only. Normalize checkout line
        # endings before the exact content comparison so CI still verifies every
        # package, version, license value, and hash rather than the Git worktree mode.
        $existing = (Get-Content -LiteralPath $noticePath -Raw) `
            -replace "`r`n", "`n" `
            -replace "`r", "`n"
        if ($existing -ne $content) {
            throw 'THIRD-PARTY-NOTICES.md does not match the resolved production dependency graph.'
        }
        Write-Host "Third-party notice verified: $($rows.Count) production packages."
    }
    else {
        Set-Content -LiteralPath $noticePath -Value $content -Encoding utf8NoBOM -NoNewline
        Write-Host "Third-party notice generated: $($rows.Count) production packages."
    }
}
finally {
    Pop-Location
}
