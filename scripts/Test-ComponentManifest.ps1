param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$ComponentsRoot
)

$ErrorActionPreference = 'Stop'
$manifestFullPath = [System.IO.Path]::GetFullPath($ManifestPath)
$componentsFullPath = [System.IO.Path]::GetFullPath($ComponentsRoot)

if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) {
    throw "Component manifest was not found: $manifestFullPath"
}

if (-not (Test-Path -LiteralPath $componentsFullPath -PathType Container)) {
    throw "Component directory was not found: $componentsFullPath"
}

$manifest = Get-Content -LiteralPath $manifestFullPath -Raw -Encoding UTF8 | ConvertFrom-Json
$failures = [System.Collections.Generic.List[string]]::new()
$checked = 0

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    $command = Get-Command Get-FileHash -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $sha.ComputeHash($stream)
            return ([System.BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

foreach ($component in $manifest.components) {
    foreach ($file in $component.requiredFiles) {
        $relativePath = [string]$file.path
        if (-not $relativePath.StartsWith('bin\', [System.StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add("Unsupported manifest path: $relativePath")
            continue
        }

        $componentRelativePath = $relativePath.Substring(4)
        $candidatePath = [System.IO.Path]::GetFullPath((Join-Path $componentsFullPath $componentRelativePath))
        $rootWithSeparator = $componentsFullPath.TrimEnd('\') + '\'
        if (-not $candidatePath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add("Path leaves component directory: $relativePath")
            continue
        }

        if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
            $failures.Add("Missing file: $relativePath")
            continue
        }

        $expected = ([string]$file.sha256).Trim().ToLowerInvariant()
        $actual = Get-Sha256Hex -Path $candidatePath
        if ($expected -ne $actual) {
            $failures.Add("SHA-256 mismatch: $relativePath (manifest $expected, actual $actual)")
        }
        $checked++
    }
}

$proxyRoot = Join-Path $componentsFullPath 'TgWsProxyHeadless'
$proxyManifest = Join-Path $proxyRoot 'runtime-manifest.json'
if (-not (Test-Path -LiteralPath $proxyManifest -PathType Leaf)) {
    $failures.Add('Missing Telegram proxy runtime manifest')
} elseif ((Get-Sha256Hex -Path $proxyManifest) -ne '97302511ab74596d7d707ee60ad262a36f7d35a09b2a49046a1c04c89b3a8624') {
    $failures.Add('Telegram proxy runtime manifest SHA-256 mismatch')
} else {
    $proxyBundle = Get-Content -LiteralPath $proxyManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $proxyPrefix = [IO.Path]::GetFullPath($proxyRoot).TrimEnd('\') + '\'
    foreach ($file in $proxyBundle.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $proxyRoot $file.path))
        if (-not $path.StartsWith($proxyPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $failures.Add("Missing or invalid Telegram runtime path: $($file.path)")
        } elseif ((Get-Sha256Hex -Path $path) -ne $file.sha256) {
            $failures.Add("Telegram runtime SHA-256 mismatch: $($file.path)")
        }
        $checked++
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error $failure
    }
    exit 1
}

Write-Host "Component manifest OK: $checked files verified."
