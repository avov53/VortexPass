param([string]$PythonPath = 'python')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$work = Join-Path $root ('components-temp\tg-proxy-rebuild-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$archive = Join-Path $work 'source.zip'
Invoke-WebRequest 'https://github.com/Flowseal/tg-ws-proxy/archive/refs/tags/v1.11.2.zip' -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne '1623427a1b5c523df79c38c88c1600bfa7d6c44550469858046f1cc3c4c766f1') {
    throw 'Upstream source checksum mismatch'
}
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $work 'source')
& $PythonPath -m venv (Join-Path $work 'env')
if ($LASTEXITCODE -ne 0) { throw 'Python environment creation failed' }
$python = Join-Path $work 'env\Scripts\python.exe'
& $python -m pip install -r (Join-Path $PSScriptRoot 'tg-ws-proxy-build-requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed' }
$source = Join-Path $work 'source\tg-ws-proxy-1.11.2'
$extraData = @()
# The bundled Codex Python adds a trust-policy file to ssl.SSLContext.
# Keep that policy when using this runtime to build the portable executable.
$trustFile = & $python -c "import ssl, os; print(os.path.join(os.path.dirname(ssl.__file__), 'codex-rejected-cas.pem'))"
if (Test-Path -LiteralPath $trustFile) { $extraData = @('--add-data', ($trustFile + ';.')) }
& $python -m PyInstaller --noconfirm --onedir --console --name TgWsProxyHeadless --paths $source --hidden-import h2 --collect-data certifi @extraData --distpath (Join-Path $work 'dist') --workpath (Join-Path $work 'build') --specpath $work (Join-Path $PSScriptRoot 'tg_ws_headless.py')
if ($LASTEXITCODE -ne 0) { throw 'Proxy build failed' }
$bundle = Join-Path $work 'dist\TgWsProxyHeadless'
& (Join-Path $bundle 'TgWsProxyHeadless.exe') --help
if ($LASTEXITCODE -ne 0) { throw 'Proxy CLI smoke check failed' }
$files = @(Get-ChildItem -LiteralPath $bundle -File -Recurse | Sort-Object FullName | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($bundle, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
@{ version = '1.11.2'; files = $files } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $bundle 'runtime-manifest.json') -Encoding utf8
Get-FileHash -LiteralPath (Join-Path $bundle 'TgWsProxyHeadless.exe'), (Join-Path $bundle 'runtime-manifest.json') -Algorithm SHA256
Write-Host "Candidate bundle: $bundle. Review it and update the pinned hashes before replacing components/zapret/TgWsProxyHeadless."
