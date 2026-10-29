param([string]$BundleRoot = (Join-Path $PSScriptRoot '..\components\local-ai'))
$ErrorActionPreference = 'Stop'
function Get-LocalAiHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
$bundle = Get-Content -LiteralPath (Join-Path $BundleRoot 'manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in $bundle.files) {
    if ([IO.Path]::GetFileName($entry.path) -ne $entry.path -or $entry.path.Contains(':')) { throw 'Invalid AI asset path.' }
    $file = Get-Item -LiteralPath (Join-Path $BundleRoot $entry.path)
    if ($file.Length -ne $entry.size -or (Get-LocalAiHash $file.FullName) -ne $entry.sha256) {
        throw "Local AI asset verification failed: $($entry.path)"
    }
}
foreach ($required in @('llama-cli.exe','qwen2.5-0.5b-instruct-q4_k_m.gguf','QWEN-LICENSE.txt','LLAMA-LICENSE.txt','SOURCE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $BundleRoot $required))) { throw "Missing AI asset: $required" }
}
Write-Host "Local AI bundle verified: $($bundle.name)"
