param([string]$Version=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim())
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$pinned=(Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
if($Version -ne $pinned){throw 'Version must match VERSION. Change the version source before publishing.'}
$frontend=Get-Content -LiteralPath (Join-Path $root 'src/VortexPass.App/web-src/package.json') -Raw | ConvertFrom-Json
if($frontend.version -ne $Version){throw 'Frontend package version must match VERSION.'}
$releaseRoot=Join-Path $root "artifacts/release-v$Version"
$publish=Join-Path $releaseRoot 'publish'
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if(Test-Path -LiteralPath $publish){
 $resolved=(Resolve-Path -LiteralPath $publish).Path
 if(!$resolved.StartsWith((Join-Path $root 'artifacts')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe publish cleanup path'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
Push-Location (Join-Path $root 'src/VortexPass.App/web-src')
try{pnpm run build;if($LASTEXITCODE){throw 'Frontend build failed'}}finally{Pop-Location}
foreach($project in @('App','Updater','DnsGuard')){
 $output=if($project -eq 'App'){$publish}elseif($project -eq 'Updater'){Join-Path $publish 'updater'}else{Join-Path $publish 'dns-guard'}
 dotnet publish (Join-Path $root "src/VortexPass.$project/VortexPass.$project.csproj") -c Release -r win-x64 --self-contained true -p:NuGetAudit=false "-p:Version=$Version" -o $output
 if($LASTEXITCODE){throw "$project publish failed"}
}
New-Item -ItemType Directory -Path (Join-Path $publish 'bin') -Force | Out-Null
Copy-Item -Path (Join-Path $root 'components/zapret/*') -Destination (Join-Path $publish 'bin') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish
Get-ChildItem -LiteralPath $publish -File -Recurse -Filter '*.pdb' | ForEach-Object {Remove-Item -LiteralPath $_.FullName -Force}
& (Join-Path $root 'scripts/Test-ComponentManifest.ps1') -ManifestPath (Join-Path $publish 'assets/external-components.json') -ComponentsRoot (Join-Path $publish 'bin')
& (Join-Path $root 'scripts/Test-LocalAiBundle.ps1') -BundleRoot (Join-Path $publish 'local-ai')
foreach($relative in @('VortexPass.dll','updater/VortexPass.Updater.dll','dns-guard/VortexPass.DnsGuard.dll')){
 if((Get-Item -LiteralPath (Join-Path $publish $relative)).VersionInfo.ProductVersion.Split('+')[0] -ne $Version){throw "Wrong embedded version: $relative"}
}
$private=Get-ChildItem -LiteralPath $publish -File -Recurse | Where-Object {$_.Name -match '^(settings|selected-profile|profile|update-state)\.json$|\.log$|\.pfx$|\.key$'}
if($private){throw 'Private state was found in publish output'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package=Join-Path $releaseRoot 'VortexPass-win-x64.zip'
if(Test-Path -LiteralPath $package){Remove-Item -LiteralPath $package -Force}
[IO.Compression.ZipFile]::CreateFromDirectory($publish,$package,[IO.Compression.CompressionLevel]::Optimal,$false)
$hash=(Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($package+'.sha256') -Value "$hash  VortexPass-win-x64.zip" -NoNewline -Encoding ascii
"Release ready: $package"
