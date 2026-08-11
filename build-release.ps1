param(
  [Parameter(Mandatory = $true)] [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'
$package = Join-Path $root 'artifacts\VortexPass-win-x64.zip'

$pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
if ($pnpm) {
    Push-Location (Join-Path $root 'src\VortexPass.App\web-src')
    try { & $pnpm.Source run build; if ($LASTEXITCODE -ne 0) { throw 'Web interface build failed.' } }
    finally { Pop-Location }
} elseif (-not (Test-Path -LiteralPath (Join-Path $root 'src\VortexPass.App\web-dist\index.html'))) {
    throw 'pnpm was not found and the prebuilt web interface is missing.'
}

Remove-Item -LiteralPath $publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publish -Force | Out-Null
dotnet publish (Join-Path $root 'src\VortexPass.App\VortexPass.App.csproj') -c Release -r win-x64 --self-contained true -p:Version=$Version -p:AssemblyInformationalVersion=$Version -o $publish
dotnet publish (Join-Path $root 'src\VortexPass.Updater\VortexPass.Updater.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $publish 'updater')
Copy-Item -LiteralPath (Join-Path $root 'components\zapret') -Destination (Join-Path $publish 'bin') -Recurse -Force
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $package -Force
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$package.sha256" -Value "$hash  VortexPass-win-x64.zip" -NoNewline
Write-Host "Release package: $package"
Write-Host "Upload VortexPass-win-x64.zip and VortexPass-win-x64.zip.sha256 to GitHub release v$Version"
