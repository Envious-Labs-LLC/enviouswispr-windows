param(
    [switch]$RequireApproved
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$inventoryPath = Join-Path $repoRoot 'docs\distribution\artifact-license-inventory.json'

function Require-ExactProperties(
    [string]$label,
    [object]$value,
    [string[]]$expected)
{
    $actual = @($value.PSObject.Properties.Name | Sort-Object)
    $wanted = @($expected | Sort-Object)
    if (Compare-Object -ReferenceObject $wanted -DifferenceObject $actual)
    {
        throw "$label has unexpected or missing properties."
    }
}

if (-not (Test-Path -LiteralPath $inventoryPath -PathType Leaf))
{
    throw 'The model/native artifact license inventory is missing.'
}

$inventory = Get-Content -LiteralPath $inventoryPath -Raw |
    ConvertFrom-Json -ErrorAction Stop
Require-ExactProperties 'Artifact inventory' $inventory @(
    'schemaVersion',
    'evidenceObservedAt',
    'artifacts')
$evidenceObservedAt = [DateTimeOffset]::MinValue
if ($inventory.schemaVersion -ne 1 -or
    -not [DateTimeOffset]::TryParse(
        [string]$inventory.evidenceObservedAt,
        [ref]$evidenceObservedAt))
{
    throw 'The artifact inventory schema or evidence date is invalid.'
}

$requiredIds = @(
    'parakeet-final-model',
    'whisper-final-model',
    'whisper-preview-model',
    'eg1-model',
    'llama-server-windows',
    'nvidia-cuda-runtime',
    'nvidia-cudnn-runtime',
    'minds14-public-uat-fixtures')
$expectedEvidence = @{
    'parakeet-final-model' = @(
        'CC-BY-4.0',
        'https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3')
    'whisper-final-model' = @(
        'MIT',
        'https://github.com/openai/whisper/blob/main/LICENSE')
    'whisper-preview-model' = @(
        'MIT',
        'https://github.com/openai/whisper/blob/main/LICENSE')
    'eg1-model' = @(
        'UPSTREAM-APACHE-2.0-DERIVATIVE-TERMS-PENDING',
        'https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507')
    'llama-server-windows' = @(
        'MIT',
        'https://github.com/ggml-org/llama.cpp/blob/master/LICENSE')
    'nvidia-cuda-runtime' = @(
        'LicenseRef-NVIDIA-CUDA-EULA',
        'https://docs.nvidia.com/cuda/eula/')
    'nvidia-cudnn-runtime' = @(
        'LicenseRef-NVIDIA-cuDNN-SLA',
        'https://docs.nvidia.com/deeplearning/cudnn/backend/latest/reference/eula.html')
    'minds14-public-uat-fixtures' = @(
        'CC-BY-4.0',
        'https://huggingface.co/datasets/PolyAI/minds14/tree/40ce77cb32a384e4d50a568e1ec39ac804019d33')
}
$artifacts = @($inventory.artifacts)
$artifactIdDifference = @(Compare-Object `
    -ReferenceObject @($requiredIds | Sort-Object) `
    -DifferenceObject @($artifacts.id | Sort-Object))
if ($artifacts.Count -ne $requiredIds.Count -or
    $artifactIdDifference.Count -ne 0)
{
    throw 'The artifact inventory does not contain the exact required artifact set.'
}

$pending = [System.Collections.Generic.List[string]]::new()
foreach ($artifact in $artifacts)
{
    Require-ExactProperties "Artifact $($artifact.id)" $artifact @(
        'id',
        'artifactClass',
        'plannedPayload',
        'upstreamIdentity',
        'observedLicense',
        'sourceUrl',
        'sourceRevision',
        'shipDisposition',
        'approvedBy',
        'approvedAt',
        'openRequirements')

    foreach ($field in @(
        'id',
        'artifactClass',
        'plannedPayload',
        'upstreamIdentity',
        'observedLicense',
        'sourceRevision'))
    {
        if ([string]::IsNullOrWhiteSpace([string]$artifact.$field))
        {
            throw "Artifact $($artifact.id) has an empty $field."
        }
    }

    $source = $null
    if (-not [Uri]::TryCreate([string]$artifact.sourceUrl, [UriKind]::Absolute, [ref]$source) -or
        $source.Scheme -ne [Uri]::UriSchemeHttps)
    {
        throw "Artifact $($artifact.id) has an invalid source URL."
    }

    $expected = $expectedEvidence[[string]$artifact.id]
    if ($artifact.observedLicense -cne $expected[0] -or
        $artifact.sourceUrl -cne $expected[1])
    {
        throw "Artifact $($artifact.id) does not match its reviewed upstream evidence."
    }

    if ($artifact.artifactClass -notin @('model', 'native-runtime', 'test-data'))
    {
        throw "Artifact $($artifact.id) has an unsupported artifact class."
    }

    if ($artifact.shipDisposition -notin @('pending-review', 'approved-to-ship'))
    {
        throw "Artifact $($artifact.id) has an unsupported ship disposition."
    }

    $requirements = @($artifact.openRequirements)
    if (@($requirements | Where-Object {
                [string]::IsNullOrWhiteSpace([string]$_) -or
                ([string]$_).Length -gt 300
            }).Count -gt 0)
    {
        throw "Artifact $($artifact.id) has an invalid open requirement."
    }
    $approved = $artifact.shipDisposition -eq 'approved-to-ship'
    if ($approved)
    {
        $approvedAt = [DateTimeOffset]::MinValue
        if ($requirements.Count -ne 0 -or
            [string]::IsNullOrWhiteSpace([string]$artifact.approvedBy) -or
            -not [DateTimeOffset]::TryParse([string]$artifact.approvedAt, [ref]$approvedAt) -or
            $artifact.sourceRevision -eq 'PENDING' -or
            $artifact.observedLicense -match 'PENDING|UNKNOWN')
        {
            throw "Artifact $($artifact.id) has an incomplete approval record."
        }
    }
    else
    {
        if ($requirements.Count -eq 0 -or
            $null -ne $artifact.approvedBy -or
            $null -ne $artifact.approvedAt)
        {
            throw "Artifact $($artifact.id) has an inconsistent pending record."
        }
        $pending.Add([string]$artifact.id)
    }
}

if ($RequireApproved -and $pending.Count -gt 0)
{
    throw "Artifact license approval is incomplete: $($pending -join ', ')"
}

Write-Host "Artifact license inventory validated: $($artifacts.Count) records, $($pending.Count) pending approval."
