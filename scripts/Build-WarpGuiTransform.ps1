param([string]$InstallerPath,[string]$OutputPath)
$ErrorActionPreference='Stop'
$installer=[IO.Path]::GetFullPath($InstallerPath)
$output=[IO.Path]::GetFullPath($OutputPath)
if((Get-FileHash -LiteralPath $installer).Hash -ne 'AB3138DBEBDF132AF076B5EBCB25B42345D29258ACB76267C3BD1F8CFEA57A53'){throw 'Unexpected WARP MSI: transform must be reviewed for this version'}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('vortexpass-warp-'+[guid]::NewGuid()+'.msi')
try {
 Copy-Item -LiteralPath $installer -Destination $temp
 $engine=New-Object -ComObject WindowsInstaller.Installer
 $original=$engine.OpenDatabase($installer,0)
 $modified=$engine.OpenDatabase($temp,1)
 $query=$modified.OpenView('SELECT `Condition` FROM `InstallExecuteSequence` WHERE `Action`=''LaunchGUICA''')
 $query.Execute();$row=$query.Fetch()
 if(!$row -or $row.StringData(1) -ne '(NOT REMOVE) OR (REINSTALL)'){throw 'Unexpected LaunchGUICA condition'}
 $query.Close()
 $update=$modified.OpenView('UPDATE `InstallExecuteSequence` SET `Condition`=''0'' WHERE `Action`=''LaunchGUICA''')
 $update.Execute();$update.Close();$modified.Commit()
 if(Test-Path -LiteralPath $output){Remove-Item -LiteralPath $output}
 $modified.GenerateTransform($original,$output)|Out-Null
 $modified.CreateTransformSummaryInfo($original,$output,0,0)|Out-Null
 Write-Output ('WARP GUI transform: '+(Get-FileHash -LiteralPath $output).Hash)
} finally {
 if($modified){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($modified)}
 if($original){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($original)}
 if($engine){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)}
 [GC]::Collect();[GC]::WaitForPendingFinalizers()
 Remove-Item -LiteralPath $temp -ErrorAction SilentlyContinue
}
