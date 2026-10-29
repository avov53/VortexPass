param([string]$ArchivePath,[string]$SourceVersion='0.0.8')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
if(!$ArchivePath){
 $cache=Join-Path $root 'components-temp/telegram-runtime';New-Item -ItemType Directory -Path $cache -Force | Out-Null
 $ArchivePath=Join-Path $cache 'VortexPass-win-x64.zip'
 $base="https://github.com/avov53/VortexPass/releases/download/v$SourceVersion"
 $hash=(Invoke-RestMethod "$base/VortexPass-win-x64.zip.sha256").Split()[0]
 if(!(Test-Path -LiteralPath $ArchivePath) -or (Get-FileHash -LiteralPath $ArchivePath).Hash -ne $hash){Invoke-WebRequest "$base/VortexPass-win-x64.zip" -OutFile $ArchivePath}
 if((Get-FileHash -LiteralPath $ArchivePath).Hash -ne $hash){throw 'Release ZIP hash mismatch'}
}
$policy=Get-Content -LiteralPath (Join-Path $root 'src/VortexPass.Core/Services/TelegramDesktopProxyService.cs') -Raw
$expected=[regex]::Match($policy,'ManifestSha256 = "([a-f0-9]+)"').Groups[1].Value
$destination=Join-Path $root 'components/zapret/TgWsProxyHeadless';New-Item -ItemType Directory -Path $destination -Force | Out-Null
$destination=[IO.Path]::GetFullPath($destination)+[IO.Path]::DirectorySeparatorChar
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($ArchivePath)
try{
 $manifest=$zip.GetEntry('bin/TgWsProxyHeadless/runtime-manifest.json')
 if(!$manifest){throw 'Pinned runtime is missing in ZIP'}
 $temporary=Join-Path $destination 'runtime-manifest.json'
 [IO.Compression.ZipFileExtensions]::ExtractToFile($manifest,$temporary,$true)
 if((Get-FileHash -LiteralPath $temporary).Hash -ne $expected){throw 'Pinned Telegram manifest mismatch'}
 $entries=(Get-Content -LiteralPath $temporary -Raw | ConvertFrom-Json).files
 foreach($file in $entries){
  $entry=$zip.GetEntry('bin/TgWsProxyHeadless/'+$file.path.Replace('\','/'))
  if(!$entry){throw 'Runtime file missing'}
  $path=[IO.Path]::GetFullPath((Join-Path $destination $file.path))
  if(!$path.StartsWith($destination,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe runtime path'}
  New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force | Out-Null
  [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$path,$true)
  if((Get-FileHash -LiteralPath $path).Hash -ne $file.sha256){throw 'Pinned runtime file mismatch'}
 }
}finally{$zip.Dispose()}
'Pinned Telegram runtime restored from release ZIP.'
