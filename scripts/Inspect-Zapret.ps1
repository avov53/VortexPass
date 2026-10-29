param([switch]$IncludeArguments)
$ErrorActionPreference = 'Stop'
# Read-only: never starts/stops a service, flushes DNS or changes TCP options.
function CommandHash([string]$command) {
    if (-not $command) { return $null }
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($command)))).Replace('-','').ToLowerInvariant() }
    finally { $hash.Dispose() }
}
$service = Get-CimInstance Win32_Service -Filter "Name='zapret'"
$strategy = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\zapret' -ErrorAction SilentlyContinue).'zapret-discord-youtube'
$processes = @(Get-CimInstance Win32_Process -Filter "Name='winws.exe' OR Name='winws2.exe'" | ForEach-Object {
    $hash = if ($_.ExecutablePath -and (Test-Path -LiteralPath $_.ExecutablePath)) { (Get-FileHash -LiteralPath $_.ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    [pscustomobject]@{ pid=$_.ProcessId; executableHash=$hash; commandHash=(CommandHash $_.CommandLine); command=$(if($IncludeArguments){$_.CommandLine}else{$null}) }
})
[pscustomobject]@{
    readOnly=$true; running=$processes; strategy=$strategy;
    serviceState=$service.State; serviceCommandHash=(CommandHash $service.PathName);
    serviceCommand=$(if($IncludeArguments){$service.PathName}else{$null});
    tcpGlobal=((& netsh.exe interface tcp show global) -join [Environment]::NewLine)
} | ConvertTo-Json -Depth 4
