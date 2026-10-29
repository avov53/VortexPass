param([string]$BundleRoot=(Join-Path $PSScriptRoot '..\components\warp-installer'))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $BundleRoot -Force | Out-Null
$path=Join-Path $BundleRoot 'Cloudflare_WARP_Release-x64.msi'
$expected='ab3138dbebdf132af076b5ebcb25b42345d29258acb76267c3bd1f8cfea57a53'
if(!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $expected){
 Invoke-WebRequest 'https://1111-releases.cloudflareclient.com/windows/Cloudflare_WARP_Release-x64.msi' -OutFile ($path+'.download')
 if((Get-FileHash -LiteralPath ($path+'.download')).Hash -ne $expected){throw 'Official MSI changed; update the pin and transform after review. Do not publish.'}
 Move-Item -LiteralPath ($path+'.download') -Destination $path -Force
}
$signature=Get-AuthenticodeSignature -LiteralPath $path
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Cloudflare'){throw 'Cloudflare Authenticode verification failed'}
& (Join-Path $PSScriptRoot 'Build-WarpGuiTransform.ps1') -InstallerPath $path -OutputPath (Join-Path $BundleRoot 'suppress-gui.mst')
