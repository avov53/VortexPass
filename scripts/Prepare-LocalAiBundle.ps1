param([string]$BundleRoot = (Join-Path $PSScriptRoot '..\components\local-ai'))
$ErrorActionPreference = 'Stop'
$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
$manifest = Get-Content -LiteralPath (Join-Path $BundleRoot 'manifest.json') -Raw | ConvertFrom-Json
function Confirm-Asset([string]$Path, [string]$Hash) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') -eq $Hash }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
$model = $manifest.files | Where-Object path -eq 'qwen2.5-0.5b-instruct-q4_k_m.gguf'
$modelPath = Join-Path $BundleRoot $model.path
if (-not (Confirm-Asset $modelPath $model.sha256)) {
    $partial = $modelPath + '.download'
    Invoke-WebRequest -Uri 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/9217f5db79a29953eb74d5343926648285ec7e67/qwen2.5-0.5b-instruct-q4_k_m.gguf' -OutFile $partial
    if (-not (Confirm-Asset $partial $model.sha256)) { throw 'Official model SHA-256 mismatch.' }
    Move-Item -LiteralPath $partial -Destination $modelPath -Force
}
$runtimeFiles = $manifest.files | Where-Object { $_.path -match '\.(dll|exe)$' }
$missing = $runtimeFiles | Where-Object { -not (Confirm-Asset (Join-Path $BundleRoot $_.path) $_.sha256) }
if ($missing) {
    $downloadDirectory = Join-Path $BundleRoot ('download-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $downloadDirectory | Out-Null
    $archive = Join-Path $downloadDirectory 'runtime.zip'
    Invoke-WebRequest -Uri 'https://github.com/ggml-org/llama.cpp/releases/download/b5046/llama-b5046-bin-win-noavx-x64.zip' -OutFile $archive
    if (-not (Confirm-Asset $archive '61d252296c93c9e3f95b9f209b1f5b5effca849b62fdd6c6423d6e69638007c3')) { throw 'Official runtime SHA-256 mismatch.' }
    $expanded = Join-Path $downloadDirectory 'runtime'
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded
    foreach ($entry in $runtimeFiles) {
        $source = Join-Path $expanded $entry.path
        if (-not (Confirm-Asset $source $entry.sha256)) { throw "Runtime asset mismatch: $($entry.path)" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $BundleRoot $entry.path) -Force
    }
    $resolvedDownload = [IO.Path]::GetFullPath($downloadDirectory)
    if (-not $resolvedDownload.StartsWith($BundleRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid temporary cleanup path.' }
    Remove-Item -LiteralPath $resolvedDownload -Recurse -Force
}
& (Join-Path $PSScriptRoot 'Test-LocalAiBundle.ps1') -BundleRoot $BundleRoot
