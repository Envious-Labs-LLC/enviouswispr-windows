param(
    [string]$DataDirectory,

    [string]$ParakeetSourceDirectory,

    [string]$WhisperSourceDirectory,

    [string]$PreviewSourceDirectory,

    [string]$LlamaRuntimeSourceDirectory,

    [string]$EgOneModelPath,

    [string[]]$CudaSourceDirectories,

    [switch]$IncludeCuda,

    # THE ONLY WAY PAST THE GUARD BELOW, AND IT COSTS A STATED INTENT. Provisioning a machine that
    # has an NVIDIA card without its runtime libraries is legitimate - a deliberate processor-only
    # setup, or a card whose libraries are supplied some other way - but it is never what somebody
    # wanted by ACCIDENT, which is what the bare default used to produce.
    [switch]$SkipCuda
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

# ---- refuse to half-provision a machine that has a card ---------------------------------------
# THIS SCRIPT'S DEFAULT SILENTLY BUILT THE EXACT STATE ISSUE #102 SPENT DAYS ON. Without
# -IncludeCuda it provisions models and no graphics runtime, so the app correctly finds no
# dependencies, correctly falls back, and dictation runs about a hundred times slower on a machine
# with an idle RTX 4090. Nothing failed, nothing was red, and the only symptom was a slow app.
#
# A DEFAULT THAT PRODUCES A BROKEN-LOOKING MACHINE IS NOT A SAFE DEFAULT. The check runs BEFORE any
# file is copied so a refusal leaves the machine exactly as it was found.
#
# THE DRIVER'S OWN TOOL IS THE AUTHORITY, and the display adapter list is the fallback for a machine
# whose driver tools are not on PATH. Neither is asked whether the RUNTIME libraries are present -
# that is what -IncludeCuda provisions; this asks only whether a card is here to be wasted.
function Test-NvidiaDevicePresent {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $smi = Get-Command 'nvidia-smi' -ErrorAction SilentlyContinue
        if ($null -ne $smi) {
            $listed = & $smi.Source '-L' 2>&1 | Out-String
            if ($LASTEXITCODE -eq 0 -and $listed -match 'GPU 0:') { return $true }
        }

        $adapters = @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'NVIDIA' })
        return $adapters.Count -gt 0
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

if (-not $IncludeCuda -and -not $SkipCuda -and (Test-NvidiaDevicePresent)) {
    throw ('This machine has an NVIDIA graphics card and -IncludeCuda was not passed, so the run ' +
        'would install the models and none of the graphics runtime libraries they need. That is ' +
        'the state where transcription silently falls back to the processor and runs about a ' +
        'hundred times slower (#102). Re-run with -IncludeCuda, or with -SkipCuda if a ' +
        'processor-only machine is what you meant. Nothing has been copied.')
}

if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
        'Envious Labs\EnviousWispr-Founder'
}
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)

if ([string]::IsNullOrWhiteSpace($ParakeetSourceDirectory)) {
    $ParakeetSourceDirectory = Join-Path $repoRoot 'models\parakeet-tdt-0.6b-v3'
}
if ([string]::IsNullOrWhiteSpace($WhisperSourceDirectory)) {
    $WhisperSourceDirectory = Join-Path $repoRoot 'models\whisper-large-v3-turbo'
}
if ([string]::IsNullOrWhiteSpace($PreviewSourceDirectory)) {
    $PreviewSourceDirectory = Join-Path $repoRoot 'models\whisper-small'
}
if ([string]::IsNullOrWhiteSpace($LlamaRuntimeSourceDirectory)) {
    $LlamaRuntimeSourceDirectory = Join-Path $repoRoot 'tools\llama.cpp'
}

$ParakeetSourceDirectory = [IO.Path]::GetFullPath($ParakeetSourceDirectory)
$WhisperSourceDirectory = [IO.Path]::GetFullPath($WhisperSourceDirectory)
$PreviewSourceDirectory = [IO.Path]::GetFullPath($PreviewSourceDirectory)
$LlamaRuntimeSourceDirectory = [IO.Path]::GetFullPath($LlamaRuntimeSourceDirectory)

function Assert-SourceDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Required founder-local source directory was not found: $Path"
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Assert-SourceDirectory -Path $Source
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

Assert-SourceDirectory -Path $ParakeetSourceDirectory
Assert-SourceDirectory -Path $WhisperSourceDirectory
Assert-SourceDirectory -Path $PreviewSourceDirectory
Assert-SourceDirectory -Path $LlamaRuntimeSourceDirectory

$modelsDirectory = Join-Path $DataDirectory 'models'
$runtimeDirectory = Join-Path $DataDirectory 'runtime'
New-Item -ItemType Directory -Force -Path $modelsDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null

Write-Host 'Provisioning Parakeet final-transcription files...'
Copy-DirectoryContents `
    -Source $ParakeetSourceDirectory `
    -Destination (Join-Path $modelsDirectory 'parakeet-tdt-0.6b-v3')

Write-Host 'Provisioning Whisper final-transcription files...'
Copy-DirectoryContents `
    -Source $WhisperSourceDirectory `
    -Destination (Join-Path $modelsDirectory 'whisper-large-v3-turbo')

Write-Host 'Provisioning Whisper Live Preview files...'
Copy-DirectoryContents `
    -Source $PreviewSourceDirectory `
    -Destination (Join-Path $modelsDirectory 'whisper-small')

$llamaDestination = Join-Path $runtimeDirectory 'llama.cpp'
New-Item -ItemType Directory -Force -Path $llamaDestination | Out-Null
$llamaFiles = Get-ChildItem -LiteralPath $LlamaRuntimeSourceDirectory -File | Where-Object {
    $_.Extension -eq '.dll' -or
    $_.Name -eq 'llama-server.exe' -or
    $_.Name -like 'LICENSE*'
}
if (@($llamaFiles | Where-Object Name -eq 'llama-server.exe').Count -ne 1) {
    throw 'The llama.cpp founder runtime does not contain exactly one llama-server.exe.'
}
Write-Host 'Provisioning the founder-local llama.cpp runtime...'
$llamaFiles | Copy-Item -Destination $llamaDestination -Force

$egOneProvisioned = $false
if (-not [string]::IsNullOrWhiteSpace($EgOneModelPath)) {
    $EgOneModelPath = [IO.Path]::GetFullPath($EgOneModelPath)
    if (-not (Test-Path -LiteralPath $EgOneModelPath -PathType Leaf)) {
        throw "The founder-local EG-1 model was not found: $EgOneModelPath"
    }

    $egOneDirectory = Join-Path $modelsDirectory 'eg-1'
    New-Item -ItemType Directory -Force -Path $egOneDirectory | Out-Null
    Write-Host 'Provisioning the development-only founder EG-1 model...'
    Copy-Item -LiteralPath $EgOneModelPath -Destination (Join-Path $egOneDirectory 'active.gguf') -Force
    $egOneProvisioned = $true
}

$requiredCudaLibraries = @(
    'cublasLt64_13.dll',
    'cublas64_13.dll',
    'cufft64_12.dll',
    'cudart64_13.dll',
    'cudnn64_9.dll',
    'cudnn_adv64_9.dll',
    'cudnn_engines_precompiled64_9.dll',
    'cudnn_engines_runtime_compiled64_9.dll',
    'cudnn_engines_tensor_ir64_9.dll',
    'cudnn_graph64_9.dll',
    'cudnn_heuristic64_9.dll',
    'cudnn_ops64_9.dll'
)
$cudaProvisioned = $false
if ($IncludeCuda) {
    if ($null -eq $CudaSourceDirectories -or $CudaSourceDirectories.Count -eq 0) {
        $defaultCudaRoot = Join-Path `
            $repoRoot `
            'spikes\s1\venv-cuda\Lib\site-packages\nvidia'
        $CudaSourceDirectories = @($defaultCudaRoot)
    }

    $resolvedCudaSources = @($CudaSourceDirectories | ForEach-Object {
        $resolved = [IO.Path]::GetFullPath($_)
        Assert-SourceDirectory -Path $resolved
        $resolved
    })
    $cudaDestination = Join-Path $runtimeDirectory 'cuda'
    New-Item -ItemType Directory -Force -Path $cudaDestination | Out-Null
    Write-Host 'Provisioning the owned CUDA and cuDNN dependency set...'
    foreach ($libraryName in $requiredCudaLibraries) {
        $matches = @($resolvedCudaSources | ForEach-Object {
            Get-ChildItem -LiteralPath $_ -Filter $libraryName -File -Recurse
        })
        if ($matches.Count -ne 1) {
            throw "Expected exactly one source for $libraryName, found $($matches.Count)."
        }

        Copy-Item -LiteralPath $matches[0].FullName -Destination $cudaDestination -Force
    }
    $cudaProvisioned = $true
}

$requiredModelFiles = @(
    (Join-Path $modelsDirectory 'parakeet-tdt-0.6b-v3\encoder-model.int8.onnx'),
    (Join-Path $modelsDirectory 'parakeet-tdt-0.6b-v3\encoder-model.onnx'),
    (Join-Path $modelsDirectory 'whisper-large-v3-turbo\ggml-large-v3-turbo-q5_0.bin'),
    (Join-Path $modelsDirectory 'whisper-large-v3-turbo\ggml-large-v3-turbo.bin'),
    (Join-Path $modelsDirectory 'whisper-small\ggml-small-q5_1.bin'),
    (Join-Path $llamaDestination 'llama-server.exe')
)
if (@($requiredModelFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -gt 0) {
    throw 'Founder-local provisioning completed with one or more required runtime files missing.'
}
if ($egOneProvisioned -and
    -not (Test-Path -LiteralPath (Join-Path $modelsDirectory 'eg-1\active.gguf') -PathType Leaf)) {
    throw 'Founder-local EG-1 provisioning completed without active.gguf.'
}
if ($cudaProvisioned -and
    @($requiredCudaLibraries | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $runtimeDirectory "cuda\$_") -PathType Leaf)
    }).Count -gt 0) {
    throw 'Founder-local CUDA provisioning completed with one or more dependencies missing.'
}

$receipt = [ordered]@{
    schemaVersion = 1
    preparedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    channel = 'founder'
    parakeet = $true
    whisper = $true
    livePreview = $true
    egOneDevelopmentModel = $egOneProvisioned
    cuda = $cudaProvisioned
}
$receiptPath = Join-Path $DataDirectory 'founder-local-provisioning.json'
$receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8

Write-Host "Founder-local runtime is ready at $DataDirectory"
Write-Output $receiptPath
