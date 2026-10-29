param([string]$Binary,[string]$Output)
Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;
public static class IconResources {
[DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr LoadLibraryEx(string p,IntPtr z,uint flags);
[DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr h);
[DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindResource(IntPtr h,IntPtr name,IntPtr type);
[DllImport("kernel32.dll")] public static extern IntPtr LoadResource(IntPtr h,IntPtr resource);
[DllImport("kernel32.dll")] public static extern IntPtr LockResource(IntPtr r);
[DllImport("kernel32.dll")] public static extern uint SizeofResource(IntPtr h,IntPtr r);
public delegate bool Callback(IntPtr h,IntPtr type,IntPtr name,IntPtr extra);
[DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern bool EnumResourceNames(IntPtr h,IntPtr type,Callback cb,IntPtr extra);
public static byte[] Read(IntPtr h,IntPtr name,int type){var r=FindResource(h,name,(IntPtr)type);if(r==IntPtr.Zero)throw new Exception("Resource absent");var data=new byte[SizeofResource(h,r)];Marshal.Copy(LockResource(LoadResource(h,r)),data,0,data.Length);return data;}
}
"@
$handle=[IconResources]::LoadLibraryEx((Resolve-Path -LiteralPath $Binary).Path,[IntPtr]::Zero,2)
if($handle -eq [IntPtr]::Zero){throw 'Cannot read PE'}
try {
$script:groups=@();$cb=[IconResources+Callback]{param($h,$t,$n,$e) $script:groups+=,[IconResources]::Read($h,$n,14);return $true}
[void][IconResources]::EnumResourceNames($handle,[IntPtr]14,$cb,[IntPtr]::Zero)
$groupIndex=0
foreach($group in $script:groups){
$count=[BitConverter]::ToUInt16($group,4);$frames=@();for($i=0;$i -lt $count;$i++){$id=[BitConverter]::ToUInt16($group,6+14*$i+12);$frames+=,[IconResources]::Read($handle,[IntPtr][int]$id,3)}
$path="$Output-$groupIndex.ico";$stream=[IO.File]::Create($path);$writer=[IO.BinaryWriter]::new($stream);$writer.Write([byte[]]$group[0..5]);$offset=6+16*$count
for($i=0;$i -lt $count;$i++){$start=6+14*$i;$writer.Write([byte[]]$group[$start..($start+11)]);$writer.Write([uint32]$offset);$offset+=$frames[$i].Length}
foreach($frame in $frames){$writer.Write([byte[]]$frame)};$writer.Dispose();"Group $groupIndex : $count frames; $path";$groupIndex++
}
}finally{[void][IconResources]::FreeLibrary($handle)}
