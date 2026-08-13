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
        $actual = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) {
            $failures.Add("SHA-256 mismatch: $relativePath (manifest $expected, actual $actual)")
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
