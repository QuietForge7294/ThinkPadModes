#requires -Version 7.0
<#
Read-only measurement, including Intel RAPL package energy and optional NVIDIA board power.
Battery watts describe the battery discharge of the whole PC, NEVER watts per process.
On AC, this script cannot measure whole-PC wall power: BatteryDischarge_W stays blank.
Memory: private committed bytes are NOT private resident RAM. Resident working sets can
share pages; their sums are not unique physical memory. Modern Windows exposes private
resident bytes via PROCESS_MEMORY_COUNTERS_EX2; unavailable fields remain blank.
Official API references:
https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-system_battery_state
https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes
https://learn.microsoft.com/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2
https://learn.microsoft.com/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo
#>
[CmdletBinding()]
param(
    [ValidateRange(0.1,60)][double]$Minutes=5,
    [ValidateRange(1,60)][int]$IntervalSeconds=5,
    [ValidateRange(0,300)][int]$SettleSeconds=30,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Phase='mesure',
    [string]$OutputDirectory=(Join-Path $PSScriptRoot 'results'),
    [string]$Conditions='',
    [switch]$Light,
    [switch]$GpuPassive,
    [switch]$NoOpen,
    [switch]$SelfTest
)
$ErrorActionPreference='Stop'

function Get-SystemCpuPercent($Before,$After){
    $idle=[double]$After.Idle-[double]$Before.Idle
    $kernel=[double]$After.Kernel-[double]$Before.Kernel
    $user=[double]$After.User-[double]$Before.User
    $total=$kernel+$user
    if($total -le 0 -or $idle -lt 0 -or $kernel -lt 0 -or $user -lt 0 -or $idle -gt $total){return $null}
    return 100.0*($total-$idle)/$total # GetSystemTimes Kernel includes Idle.
}
function Get-ProcessCpuDelta($Before,$After,[int]$LogicalProcessors){
    if(!$Before -or !$After -or $Before.Key -ne $After.Key){return $null}
    $elapsed=([double]$After.MonotonicTicks-[double]$Before.MonotonicTicks)/[Diagnostics.Stopwatch]::Frequency
    $cpu=[double]$After.CpuSeconds-[double]$Before.CpuSeconds
    if($elapsed -le 0 -or $cpu -lt 0 -or $LogicalProcessors -lt 1){return $null}
    [pscustomobject]@{CpuSeconds=$cpu;Elapsed_s=$elapsed;PercentMachine=100.0*$cpu/$elapsed/$LogicalProcessors}
}
function Get-BatteryWatts($Battery,$Status){
    if($Battery.BatteryPresent -eq 1 -and $Battery.AcOnLine -eq 0 -and $Status.ACLineStatus -eq 0 -and $Battery.Discharging -eq 1 -and $Battery.Charging -eq 0 -and $Battery.Rate -lt 0 -and $Battery.Rate -ne [int]::MinValue){return -[double]$Battery.Rate/1000.0}
    return $null
}

function Get-CpuEnergyInterval($Before,$After,[double]$ExpectedSeconds) {
    if($null -eq $Before -or $null -eq $After -or $After[0] -le $Before[0] -or $After[1] -le $Before[1]) { return }
    $seconds=($After[1]-$Before[1])/1e7
    if($seconds -lt $ExpectedSeconds*0.75 -or $seconds -gt $ExpectedSeconds*1.25) { return }
    $energy=($After[0]-$Before[0])/1e12
    [pscustomobject]@{Seconds=$seconds;Wh=$energy;Watts=$energy*3600/$seconds}
}

function ConvertFrom-NvidiaPower([string]$Output) {
    $w = 0.0
    if ([double]::TryParse($Output.Trim(), [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$w) -and [double]::IsFinite($w) -and $w -ge 0) {
        return $w
    }
}

function Get-NvidiaSample([string]$State, [scriptblock]$ReadWatts) {
    $watts = if ($State -eq 'D0' -and $ReadWatts) { & $ReadWatts }
    [pscustomobject]@{State=$State; Watts=$watts; QueryAttempted=($State -eq 'D0' -and $null -ne $ReadWatts)}
}

function Format-NvidiaState([string]$State, $Watts) {
    switch ($State) {
        'D0' { if ($null -ne $Watts) { 'ON · {0:N2} W' -f $Watts } else { 'ON · W indisponibles' } }
        'D3' { 'OFF (veille)' }
        {$_ -in 'D1','D2'} { "Veille ($State)" }
        'Absent' { 'NVIDIA absente' }
        'Multiple' { 'Plusieurs GPU NVIDIA' }
        default { 'État inconnu' }
    }
}

function Read-NvidiaWatts([string]$Executable) {
    $process = [Diagnostics.Process]::new()
    try {
        $process.StartInfo.FileName = $Executable
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.CreateNoWindow = $true
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        $process.StartInfo.ArgumentList.Add('--query-gpu=power.draw')
        $process.StartInfo.ArgumentList.Add('--format=csv,noheader,nounits')
        if (!$process.Start()) { return }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(1000)) {
            $process.Kill()
            return
        }
        if ($process.ExitCode -eq 0 -and $stdout.Wait(100) -and $stderr.Wait(100)) {
            ConvertFrom-NvidiaPower $stdout.Result
        }
    } catch {
        # A missing driver sensor or a transition does not invalidate PC measurements.
    } finally { $process.Dispose() }
}

function Write-PowerReport($Summary,$Window,$Path) {
    $culture=[Globalization.CultureInfo]::GetCultureInfo('fr-FR')
    $encode={param($Value) [Net.WebUtility]::HtmlEncode([string]$Value)}
    $format={param($Value,$Unit) if($null -eq $Value){'Indisponible'}else{([double]$Value).ToString('N2',$culture)+' '+$Unit}}
    $battery=& $format $Summary.MeanBatteryDischarge_W 'W'
    $package=& $format $Summary.MeanCpuPackage_W 'W'
    $energy=& $format $Summary.CpuPackageEnergy_Wh 'Wh'
    $peak=& $format $Summary.PeakCpuPackage_W 'W'
    $usage=& $format $Summary.MeanTotalCPUPercent '%'
    $coverage=& $format ($Summary.CpuPackageCoverageFraction*100) '%'
    $gpuState=& $encode $Summary.GpuNvidiaStatus
    $gpuMean=& $format $Summary.MeanGpuNvidiaActive_W 'W'
    $gpuPeak=& $format $Summary.PeakGpuNvidiaActive_W 'W'
    $title=& $encode $Summary.Phase
    $source=& $encode $Summary.Source
    $reason=& $encode $Summary.StopReason
    $sensor=& $encode $Summary.CpuPackageStatus
    $scheme=& $encode $Summary.PowerScheme
    $brightness=& $encode $Summary.BrightnessPercent
    $conditions=& $encode $Summary.Conditions
    $duration=& $format $Summary.MeasuredWindow_s 's'
    $valid=if($Summary.BatteryPowerComparisonValid){'Comparaison batterie valide'}else{'Comparaison batterie indisponible'}
    $rows=foreach($item in $Summary.TopNames){
        $name=& $encode $item.Name
        $activity=& $format $item.MeanCPUPercentMachineOverWindow '%'
        "<tr><th scope='row'>$name</th><td>$activity</td></tr>"
    }
    if(!$rows){$rows='<tr><td colspan="2">Aucune donnée de processus</td></tr>'}
    $rows=$rows -join [Environment]::NewLine
    $points=@(foreach($sample in $Window){
        ,@([Math]::Round($sample.Elapsed_s-$Window[0].Elapsed_s,2),$sample.BatteryDischarge_W,$sample.CpuPackage_W,$sample.GpuNvidia_W)
    })
    $data=ConvertTo-Json -InputObject $points -Compress -Depth 4
    $page=@"
<!doctype html>
<html lang="fr">
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Consommation du PC · $title</title>
<style>
:root{color-scheme:light;--ink:#172838;--muted:#546777;--line:#d8e3e8;--paper:#f2f6f6;--battery:#00746a;--cpu:#7051a3}
*{box-sizing:border-box}body{margin:0;background:var(--paper);color:var(--ink);font:16px/1.55 'Segoe UI',system-ui,sans-serif}
main{max-width:1060px;margin:auto;padding:42px 24px}header{border-bottom:2px solid var(--ink);margin-bottom:22px;padding-bottom:16px}
h1{font-size:clamp(2rem,5vw,3.25rem);line-height:1.1;margin:0 0 10px;letter-spacing:-.045em}h2{font-size:1.25rem;margin:0 0 12px}
p{margin:8px 0;color:var(--muted)}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(205px,1fr));gap:12px}
.card,section{background:#fff;border:1px solid var(--line);border-radius:14px;padding:18px 20px}
.card strong{display:block;font-size:clamp(1.3rem,3vw,2rem);font-variant-numeric:tabular-nums;margin:3px 0}.battery strong{color:var(--battery)}.cpu strong{color:var(--cpu)}
section{margin-top:14px}canvas{display:block;width:100%;height:280px}.legend{display:flex;flex-wrap:wrap;gap:18px}.legend span:before{content:'';display:inline-block;width:14px;height:4px;margin:0 6px 3px 0;background:var(--battery)}.legend span+span:before{background:var(--cpu)}.legend span:nth-child(3):before{background:#9c5414}
table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:7px 4px;border-bottom:1px solid var(--line)}td:last-child,thead th:last-child{text-align:right}tbody th{font-weight:500}
</style>
<main>
<header><h1>Consommation du PC</h1><p>$title · $source · $duration · $valid</p></header>
<div class="cards">
<div class="card battery">Décharge moyenne<strong>$battery</strong></div>
<div class="card cpu">Processeur · moyenne<strong>$package</strong></div>
<div class="card">GPU NVIDIA<strong>$gpuState</strong></div>
<div class="card">Énergie du processeur<strong>$energy</strong></div>
<div class="card">Activité CPU moyenne<strong>$usage</strong></div>
</div>
<section><h2>Puissance au fil du temps</h2>
<p class="legend"><span>Décharge de la batterie</span><span>Package processeur Intel</span><span>GPU NVIDIA actif</span></p>
<canvas id="plot" role="img" aria-label="Évolution de la puissance en watts. Les données sont également disponibles dans samples.csv."></canvas>
<p>Maximum CPU sur un intervalle mesuré · $peak. Couverture CPU · $coverage. Les valeurs couvrent les intervalles valides.</p>
<p>Le package inclut le graphique Intel intégré. La décharge batterie couvre tout le PC.</p></section>
<section><h2>GPU NVIDIA</h2><p>Moyenne active · $gpuMean. Maximum relevé · $gpuPeak. Relevés avec watts · $($Summary.GpuNvidiaPowerSamples). Relevés OFF · $($Summary.GpuNvidiaOffSamples) sur $($Summary.MeasurementSamples).</p>
<p>OFF correspond à la veille D3 signalée par Windows. Les watts restent vides pendant la veille et entre les lectures du capteur.</p></section>
<section><h2>Activité CPU par application</h2><table><thead><tr><th>Application</th><th>Part du CPU total disponible</th></tr></thead><tbody>$rows</tbody></table></section>
<section><h2>Conditions de mesure</h2><p>Profil Windows · $scheme. Luminosité déclarée · $brightness. Fin · $reason.</p><p>$conditions</p><p>Capteur CPU · $sensor.</p></section>
</main>
<script>
const points=$data;
const canvas=document.getElementById('plot');
function draw(){
  const w=canvas.clientWidth,h=canvas.clientHeight,dpr=window.devicePixelRatio||1;
  canvas.width=w*dpr;canvas.height=h*dpr;
  const ctx=canvas.getContext('2d');ctx.scale(dpr,dpr);ctx.font='12px Segoe UI, sans-serif';
  const l=38,r=w-18,t=24,b=h-32;
  const values=points.flatMap(row=>row.slice(1)).filter(Number.isFinite);
  ctx.fillStyle='#546777';
  if(!values.length){ctx.fillText('Aucune donnée de puissance valide.',l,48);return;}
  const step=Math.max(1,Math.ceil(Math.max(...values)/5));
  const ymax=5*step,xmax=Math.max(1,points.at(-1)[0]);
  const x=s=>l+(r-l)*s/xmax,y=v=>b-(b-t)*v/ymax;
  for(let i=0;i<=5;i++){
    const z=y(i*step);ctx.strokeStyle='#d8e3e8';ctx.beginPath();ctx.moveTo(l,z);ctx.lineTo(r,z);ctx.stroke();
    ctx.fillText(i*step,3,z+4);
  }
  ctx.fillText('W',3,12);ctx.fillText('0 s',l,b+21);ctx.textAlign='right';ctx.fillText(Math.round(xmax)+' s',r,b+21);
  for(const [index,color] of [[1,'#00746a'],[2,'#7051a3'],[3,'#9c5414']]){
    ctx.lineWidth=2;ctx.strokeStyle=color;ctx.fillStyle=color;ctx.beginPath();let connected=false;
    for(const row of points){
      const value=row[index];if(!Number.isFinite(value)){connected=false;continue;}
      const px=x(row[0]),py=y(value);if(connected){ctx.lineTo(px,py);}else{ctx.moveTo(px,py);}
      connected=true;
    }
    ctx.stroke();
    for(const row of points){if(Number.isFinite(row[index])){ctx.fillRect(x(row[0])-1,y(row[index])-1,2,2);}}
  }
}
window.addEventListener('resize',draw);draw();
</script>
</html>
"@
    Set-Content -LiteralPath $Path -Value $page -Encoding utf8
}

function Assert-Near([double]$Actual,[double]$Expected,[string]$Name){if([Math]::Abs($Actual-$Expected) -gt 0.00001){throw "Self-test failed: $Name ($Actual vs $Expected)"}}
if($SelfTest){
    Assert-Near (Get-SystemCpuPercent @{Idle=100;Kernel=200;User=100} @{Idle=1100;Kernel=3200;User=1100}) 75 'system CPU includes idle once'
    $frequency=[Diagnostics.Stopwatch]::Frequency
    $a=@{Key='42:100';CpuSeconds=3.0;MonotonicTicks=100*$frequency}
    $b=@{Key='42:100';CpuSeconds=4.0;MonotonicTicks=105*$frequency}
    Assert-Near (Get-ProcessCpuDelta $a $b 20).PercentMachine 1 'process CPU normalized to machine'
    $b.Key='42:200';if($null -ne (Get-ProcessCpuDelta $a $b 20)){throw 'Self-test failed: reused PID must not produce a delta'}
    $battery=@{BatteryPresent=1;AcOnLine=0;Discharging=1;Charging=0;Rate=-22560}
    $status=@{ACLineStatus=0}
    Assert-Near (Get-BatteryWatts $battery $status) 22.56 'signed discharge mW to W'
    $battery.AcOnLine=1;if($null -ne (Get-BatteryWatts $battery $status)){throw 'Self-test failed: AC must not report whole-PC watts'}
    Assert-Near ((20.0+20.0)/2*600/3600) (10.0/3) 'energy trapezoid Wh'
    Assert-Near ((50000.0-49000)/1000*3600/180) 20 'reported capacity slope W'
    if($null -ne (Get-SystemCpuPercent @{Idle=0;Kernel=0;User=0} @{Idle=0;Kernel=0;User=0})){throw 'Self-test failed: zero-time CPU interval'}
    $reading=Get-CpuEnergyInterval ([uint64[]]@(10,100)) ([uint64[]]@(25000000010,30000100)) 3
    Assert-Near $reading.Watts 30 'EMI delta pWh and 100 ns to watts'
    Assert-Near $reading.Wh 0.025 'EMI delta pWh to Wh'
    if($null -ne (Get-CpuEnergyInterval @(10,100) @(10,30000100) 3)){throw 'Self-test failed: frozen energy'}
    if($null -ne (Get-CpuEnergyInterval @(10,100) @(9,30000100) 3)){throw 'Self-test failed: counter reset'}
    if($null -ne (Get-CpuEnergyInterval @(10,100) @(20,30000100) 40)){throw 'Self-test failed: interrupted interval'}
    foreach ($state in 'D1','D2','D3','Unknown','Absent','Multiple') {
        $reading = Get-NvidiaSample $state { throw 'GPU query must be skipped outside D0' }
        if ($reading.QueryAttempted -or $null -ne $reading.Watts) { throw "Unexpected GPU watts in $state" }
    }
    if ((Format-NvidiaState 'D3' $null) -ne 'OFF (veille)') { throw 'Expected OFF in D3' }
    $reading = Get-NvidiaSample 'D0' { ConvertFrom-NvidiaPower '6.17' }
    if (!$reading.QueryAttempted) { throw 'Expected D0 GPU query' }
    Assert-Near $reading.Watts 6.17 'NVIDIA watts in invariant culture'
    foreach ($output in 'N/A', '[Not Supported]', "6.0`n7.0", 'NaN', '-1') {
        if ($null -ne (ConvertFrom-NvidiaPower $output)) { throw "Unexpected NVIDIA watts for $output" }
    }
    [pscustomobject]@{Passed=$true;Checks=27;NativeApisCalled=$false;ProcessesSampled=$false}
    return
}
if(!$IsWindows){throw 'This script requires Windows and PowerShell 7.'}

if(-not ('ManualPcPower.V20261117.NvidiaDevice' -as [type])){
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace ManualPcPower.V20261117 {

 public sealed class NvidiaDevice {
  const uint PresentDisplayDevices=0x300, BinaryProperty=0x1003;
  const string DisplayClass="{4D36E968-E325-11CE-BFC1-08002BE10318}";
  [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public uint Id; }
  [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
  static extern uint CM_Get_Device_ID_List_SizeW(out uint length,string filter,uint flags);
  [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
  static extern uint CM_Get_Device_ID_ListW(string filter,[Out] char[] buffer,uint length,uint flags);
  [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
  static extern uint CM_Locate_DevNodeW(out uint node,string instance,uint flags);
  [DllImport("cfgmgr32.dll", ExactSpelling=true)]
  static extern uint CM_Get_DevNode_PropertyW(uint node,ref PropertyKey key,out uint type,[Out] uint[] buffer,ref uint length,uint flags);

  public static string ReadState() {
   uint size;
   if(CM_Get_Device_ID_List_SizeW(out size,DisplayClass,PresentDisplayDevices)!=0 || size<1 || size>65536)
    return "Unknown";
   var ids=new char[size];
   if(CM_Get_Device_ID_ListW(DisplayClass,ids,size,PresentDisplayDevices)!=0) return "Unknown";
   string match=null;
   foreach(string id in new string(ids).Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries)) {
    if(!id.StartsWith(@"PCI\VEN_10DE&",StringComparison.OrdinalIgnoreCase)) continue;
    if(match!=null) return "Multiple";
    match=id;
   }
   if(match==null) return "Absent";
   uint node;
   if(CM_Locate_DevNodeW(out node,match,0)!=0) return "Unknown";
   var key=new PropertyKey{Format=new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),Id=32};
   var data=new uint[32];
   uint bytes=(uint)(data.Length*sizeof(uint)),type;
   if(CM_Get_DevNode_PropertyW(node,ref key,out type,data,ref bytes,0)!=0 ||
      type!=BinaryProperty || bytes<8 || data[0]<8 || data[0]>bytes || data[1]<1 || data[1]>4)
    return "Unknown";
   return "D"+(data[1]-1);
  }
 }

 public sealed class EmiMeter : IDisposable {
  static readonly Guid InterfaceId=new Guid("45BD8344-7ED6-49cf-A440-C276C933B053");
  readonly Microsoft.Win32.SafeHandles.SafeFileHandle handle;
  readonly int channelCount;
  readonly int package;
  public string Channel {get; private set;}
  [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)]
  static extern uint CM_Get_Device_Interface_List_SizeW(out uint size,ref Guid guid,string instance,uint flags);
  [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)]
  static extern uint CM_Get_Device_Interface_ListW(ref Guid guid,string instance,[Out] char[] list,uint size,uint flags);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr attributes,uint disposition,uint flags,IntPtr template);
  [DllImport("kernel32.dll",SetLastError=true)]
  [return:MarshalAs(UnmanagedType.Bool)]
  static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h,uint code,IntPtr input,uint inputSize,byte[] output,uint outputSize,out uint returned,IntPtr overlapped);
  static byte[] Read(Microsoft.Win32.SafeHandles.SafeFileHandle h,uint request,int size) {
   var buffer=new byte[size];
   uint returned;
   if(!DeviceIoControl(h,0x224000+4*request,IntPtr.Zero,0,buffer,(uint)size,out returned,IntPtr.Zero))
    throw new Win32Exception(Marshal.GetLastWin32Error());
   if(returned!=size) throw new InvalidOperationException("Réponse EMI tronquée.");
   return buffer;
  }
  EmiMeter(Microsoft.Win32.SafeHandles.SafeFileHandle h) {
   if(BitConverter.ToUInt16(Read(h,0,2),0)!=2) throw new NotSupportedException("Version EMI différente de la version 2.");
   uint size=BitConverter.ToUInt32(Read(h,1,4),0);
   if(size<68 || size>65536) throw new InvalidOperationException("Taille des métadonnées EMI invalide.");
   byte[] metadata=Read(h,2,(int)size);
   channelCount=BitConverter.ToUInt16(metadata,66);
   package=-1;
   int offset=68;
   for(int i=0;i<channelCount;i++) {
    if(offset+6>metadata.Length) throw new InvalidOperationException("Canal EMI tronqué.");
    uint unit=BitConverter.ToUInt32(metadata,offset);
    int length=BitConverter.ToUInt16(metadata,offset+4);
    if(length<2 || length%2!=0 || offset+6+length>metadata.Length)
     throw new InvalidOperationException("Nom de canal EMI invalide.");
    string name=System.Text.Encoding.Unicode.GetString(metadata,offset+6,length).TrimEnd('\0');
    if(unit==0 && name=="RAPL_Package0_PKG") {
     if(package>=0) throw new InvalidOperationException("Plusieurs canaux de package identiques.");
     package=i;
     Channel=name;
    }
    offset+=6+length;
   }
   if(package<0) throw new NotSupportedException("Canal RAPL du package Intel absent.");
   handle=h;
  }
  public static EmiMeter Open() {
   Guid guid=InterfaceId;
   uint size;
   uint result=CM_Get_Device_Interface_List_SizeW(out size,ref guid,null,0);
   if(result!=0 || size<2 || size>262144) throw new InvalidOperationException("Interface EMI non disponible ("+result+").");
   var list=new char[size];
   result=CM_Get_Device_Interface_ListW(ref guid,null,list,size,0);
   if(result!=0) throw new InvalidOperationException("Énumération EMI impossible ("+result+").");
   string failure="Canal RAPL du package Intel absent.";
   foreach(string path in new string(list).Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries)) {
    var h=CreateFileW(path,0x80000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
    try {
     if(h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
     return new EmiMeter(h);
    } catch(Exception ex) { failure=ex.Message; h.Dispose(); }
   }
   throw new InvalidOperationException(failure);
  }
  public ulong[] Sample() {
   byte[] data=Read(handle,3,checked(channelCount*16));
   return new[]{BitConverter.ToUInt64(data,package*16),BitConverter.ToUInt64(data,package*16+8)};
  }
  public void Dispose() {handle.Dispose();}
 }

 public static class Native {
  [StructLayout(LayoutKind.Sequential)] public struct BatteryState {
   public byte AcOnLine,BatteryPresent,Charging,Discharging,Spare0,Spare1,Spare2,Tag;
   public uint MaxCapacity,RemainingCapacity; public int Rate; public uint EstimatedTime,DefaultAlert1,DefaultAlert2;
  }
  [StructLayout(LayoutKind.Sequential)] public struct PowerStatus {
   public byte ACLineStatus,BatteryFlag,BatteryLifePercent,SystemStatusFlag;
   public uint BatteryLifeTime,BatteryFullLifeTime;
  }
  public struct CpuTimes {public long Idle,Kernel,User;}
  [StructLayout(LayoutKind.Sequential)] public class RamState {
   public uint Length=(uint)Marshal.SizeOf(typeof(RamState)),Load;
   public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;
  }
  [StructLayout(LayoutKind.Sequential)] struct MemoryCounters {
   public uint Size,Faults; public UIntPtr PeakWorkingSet,WorkingSet,PeakPagedPool,PagedPool,PeakNonPagedPool,NonPagedPool,Pagefile,PeakPagefile,PrivateUsage,PrivateWorkingSet;
   public ulong SharedCommit;
  }
  public class ProcessSample {
   public int Pid; public string Name,Key; public long MonotonicTicks; public double CpuSeconds;
   public ulong? PrivateCommittedBytes,WorkingSetBytes,PrivateWorkingSetBytes,ShareableWorkingSetBytes,SharedCommitBytes;
  }
  public class ProcessSnapshot {public ProcessSample[] Values;public int Enumerated,Unreadable;}
  [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level,IntPtr input,uint inputSize,out BatteryState state,uint size);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetSystemPowerStatus(out PowerStatus state);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetSystemTimes(out long idle,out long kernel,out long user);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool GlobalMemoryStatusEx([In,Out] RamState state);
  [DllImport("kernel32.dll")] static extern uint GetActiveProcessorCount(ushort group);
  [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(IntPtr process,out long creation,out long exit,out long kernel,out long user);
  [DllImport("psapi.dll",SetLastError=true)] static extern bool GetProcessMemoryInfo(IntPtr process,ref MemoryCounters info,uint size);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  public static BatteryState Battery(){BatteryState s;uint code=CallNtPowerInformation(5,IntPtr.Zero,0,out s,(uint)Marshal.SizeOf(typeof(BatteryState)));if(code!=0)throw new InvalidOperationException("SystemBatteryState NTSTATUS=0x"+code.ToString("X8"));return s;}
  public static PowerStatus Status(){PowerStatus s;if(!GetSystemPowerStatus(out s))throw new Win32Exception(Marshal.GetLastWin32Error());return s;}
  public static CpuTimes Cpu(){CpuTimes t;if(!GetSystemTimes(out t.Idle,out t.Kernel,out t.User))throw new Win32Exception(Marshal.GetLastWin32Error());return t;}
  public static RamState Ram(){var s=new RamState();if(!GlobalMemoryStatusEx(s))throw new Win32Exception(Marshal.GetLastWin32Error());return s;}
  public static int LogicalProcessors(){uint count=GetActiveProcessorCount(0xffff);return count>0?(int)count:Environment.ProcessorCount;}
  public static ProcessSnapshot Processes(){
   var result=new List<ProcessSample>();Process[] all=Process.GetProcesses();int unreadable=0;
   // EX2 is guaranteed on Windows 11 23H2+. Older systems retain EX counters
   // and leave private/shareable resident statistics unknown, never guessed.
   bool ex2=Environment.OSVersion.Version.Build>=22631;
   foreach(var p in all){IntPtr h=IntPtr.Zero;try{
    int pid=p.Id;if(pid==0)continue; // Idle is already accounted for by GetSystemTimes.
    h=OpenProcess(0x1000,false,pid);if(h==IntPtr.Zero){unreadable++;continue;}
    long created,exit,kernel,user;if(!GetProcessTimes(h,out created,out exit,out kernel,out user)){unreadable++;continue;}
    var sample=new ProcessSample{Pid=pid,Name=p.ProcessName,Key=pid.ToString()+":"+created.ToString(),MonotonicTicks=Stopwatch.GetTimestamp(),CpuSeconds=(kernel+user)/10000000.0};
    var memory=new MemoryCounters();memory.Size=ex2?(uint)Marshal.SizeOf(typeof(MemoryCounters)):(uint)(8+9*IntPtr.Size);
    if(GetProcessMemoryInfo(h,ref memory,memory.Size)){
     sample.PrivateCommittedBytes=memory.PrivateUsage.ToUInt64();sample.WorkingSetBytes=memory.WorkingSet.ToUInt64();
     if(ex2){ulong ws=memory.WorkingSet.ToUInt64(),privateWs=memory.PrivateWorkingSet.ToUInt64();if(privateWs<=ws){sample.PrivateWorkingSetBytes=privateWs;sample.ShareableWorkingSetBytes=ws-privateWs;}sample.SharedCommitBytes=memory.SharedCommit;}
    }
    result.Add(sample);
   }catch{unreadable++;}finally{if(h!=IntPtr.Zero)CloseHandle(h);p.Dispose();}}
   return new ProcessSnapshot{Values=result.ToArray(),Enumerated=all.Length,Unreadable=unreadable};
  }
 }
}
'@
}

$duration=$Minutes*60
$run=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')+'-'+$Phase)
New-Item -ItemType Directory -Path $run -Force|Out-Null
$samples=[Collections.Generic.List[object]]::new()
$byProcess=@{};$byName=@{}
$logical=[ManualPcPower.V20261117.Native]::LogicalProcessors()
$meter=$null
$meterChannel=$null
$meterIssue=$null
$previousMeter=$null
$smi=if(!$GpuPassive){
    @(
        (Join-Path $env:WINDIR 'System32/nvidia-smi.exe')
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation/NVSMI/nvidia-smi.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
$nextGpuQuery=0.0
try {
    $meter=[ManualPcPower.V20261117.EmiMeter]::Open()
    $meterChannel=$meter.Channel
} catch { $meterIssue=$_.Exception.GetBaseException().Message }
$powerScheme=(& powercfg /getactivescheme | Out-String).Trim()
$brightness='indisponible'
if(!$Light){
    try {
        $activeBrightness=Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightness -ErrorAction Stop |
            Where-Object Active | Select-Object -First 1
        if($null -ne $activeBrightness){ $brightness="$($activeBrightness.CurrentBrightness) %" }
    } catch {}
}
$clock=[Diagnostics.Stopwatch]::StartNew()
$previousCpu=$null;$previousProcesses=@{};$previousElapsed=0.0;$firstSource=$null
$stopReason='running';$failure=$null;$sourceChanged=$false;$completed=$false;$lastDetails=@()
Write-Host "Mesure : $Minutes min apres $SettleSeconds s de stabilisation ; intervalle $IntervalSeconds s."
Write-Host 'W = decharge batterie totale. Sur secteur, puissance totale inconnue. CPU par processus ne signifie pas W par processus.'
Write-Host "Resultats : $run"
try{
    do{
        $cpu=[ManualPcPower.V20261117.Native]::Cpu()
        $processes=if($Light){[ManualPcPower.V20261117.Native+ProcessSnapshot]::new()}else{[ManualPcPower.V20261117.Native]::Processes()}
        $meterReading=$null
        if($meter){
            try { $meterReading=$meter.Sample() }
            catch { $meterIssue=$_.Exception.GetBaseException().Message }
        }
        $battery=[ManualPcPower.V20261117.Native]::Battery()
        $status=[ManualPcPower.V20261117.Native]::Status()
        $ram=[ManualPcPower.V20261117.Native]::Ram()
        $gpuState=[ManualPcPower.V20261117.NvidiaDevice]::ReadState()
        $readGpu=$null
        if($gpuState -eq 'D0' -and $smi -and $clock.Elapsed.TotalSeconds -ge [Math]::Max($SettleSeconds,$nextGpuQuery)){
            # Recheck just before starting NVIDIA's utility and allow time to return to idle.
            $gpuState=[ManualPcPower.V20261117.NvidiaDevice]::ReadState()
            if($gpuState -eq 'D0') {
                $readGpu={ Read-NvidiaWatts $smi }
                $nextGpuQuery=$clock.Elapsed.TotalSeconds+15
            }
        }
        $gpu=Get-NvidiaSample $gpuState $readGpu
        $elapsed=$clock.Elapsed.TotalSeconds;$utc=[DateTime]::UtcNow.ToString('o')
        $source=if($status.ACLineStatus -eq 1 -and $battery.AcOnLine -eq 1){'AC'}elseif($status.ACLineStatus -eq 0 -and $battery.AcOnLine -eq 0){'Battery'}else{'Unknown'}
        if(!$firstSource){$firstSource=$source}
        if($source -ne $firstSource){$sourceChanged=$true}
        $watts=Get-BatteryWatts $battery $status
        $totalCpu=if($previousCpu){Get-SystemCpuPercent $previousCpu $cpu}else{$null}
        $current=@{};$details=[Collections.Generic.List[object]]::new()
        $intervalInWindow=$previousCpu -and $previousElapsed -ge $SettleSeconds
        $cpuEnergy=Get-CpuEnergyInterval $previousMeter $meterReading ($elapsed-$previousElapsed)
        foreach($p in $processes.Values){
            $current[$p.Key]=$p
            $delta=if($previousProcesses.ContainsKey($p.Key)){Get-ProcessCpuDelta $previousProcesses[$p.Key] $p $logical}else{$null}
            $row=[pscustomobject]@{UTC=$utc;Elapsed_s=$elapsed;CpuIntervalInMeasurement=[bool]$intervalInWindow;PID=$p.Pid;ProcessIdentity=$p.Key;Name=$p.Name;Comparable=($null -ne $delta);CPUPercentMachine=$(if($delta){$delta.PercentMachine}else{$null});CPUSeconds=$(if($delta){$delta.CpuSeconds}else{$null});PrivateCommitted_MB=$(if($null -ne $p.PrivateCommittedBytes){$p.PrivateCommittedBytes/1MB}else{$null});ResidentWorkingSet_MB=$(if($null -ne $p.WorkingSetBytes){$p.WorkingSetBytes/1MB}else{$null});PrivateResidentWorkingSet_MB=$(if($null -ne $p.PrivateWorkingSetBytes){$p.PrivateWorkingSetBytes/1MB}else{$null});ShareableResidentWorkingSet_MB=$(if($null -ne $p.ShareableWorkingSetBytes){$p.ShareableWorkingSetBytes/1MB}else{$null});SharedCommit_MB=$(if($null -ne $p.SharedCommitBytes){$p.SharedCommitBytes/1MB}else{$null})}
            $details.Add($row)
            if($intervalInWindow -and $delta){
                if(!$byProcess.ContainsKey($p.Key)){$byProcess[$p.Key]=[ordered]@{PID=$p.Pid;ProcessIdentity=$p.Key;Name=$p.Name;CPUSeconds=0.0;ObservedInterval_s=0.0;LastPrivateCommitted_MB=$null;LastResidentWorkingSet_MB=$null}}
                $aggregate=$byProcess[$p.Key];$aggregate.CPUSeconds+=$delta.CpuSeconds;$aggregate.ObservedInterval_s+=$delta.Elapsed_s;$aggregate.LastPrivateCommitted_MB=$row.PrivateCommitted_MB;$aggregate.LastResidentWorkingSet_MB=$row.ResidentWorkingSet_MB
                if(!$byName.ContainsKey($p.Name)){$byName[$p.Name]=0.0};$byName[$p.Name]+=$delta.CpuSeconds
            }
        }
        $lastDetails=@($details)
        $top=@($details|Where-Object Comparable|Sort-Object CPUPercentMachine -Descending|Select-Object -First 5)
        $rank=0;foreach($entry in $top){$rank++;$entry|Select-Object *,@{n='Rank';e={$rank}}|Export-Csv -LiteralPath (Join-Path $run 'top-processes.csv') -Append -NoTypeInformation -Encoding utf8}
        $sumKnown=($details|Where-Object Comparable|Measure-Object CPUPercentMachine -Sum).Sum
        $sample=[pscustomobject]@{UTC=$utc;Phase=$Phase;Elapsed_s=$elapsed;InMeasurement=($elapsed -ge $SettleSeconds);CpuIntervalInMeasurement=[bool]$intervalInWindow;Source=$source;SourceChanged=$sourceChanged;BatteryPresent=$battery.BatteryPresent;Charging=$battery.Charging;Discharging=$battery.Discharging;BatteryPercent=$(if($status.BatteryLifePercent -le 100){$status.BatteryLifePercent}else{$null});NativeRate_mW=$battery.Rate;BatteryDischarge_W=$watts;NativeRemainingCapacity=$battery.RemainingCapacity;NativeMaxCapacity=$battery.MaxCapacity;CapacityUnitsIndependentlyVerified=$false;WindowsEnergySaver=$status.SystemStatusFlag;TotalCPUPercent=$totalCpu;KnownProcessCPUPercent=$sumKnown;EnumeratedProcesses=$processes.Enumerated;ReadableProcesses=$processes.Values.Length;UnreadableProcesses=$processes.Unreadable;ComparableProcesses=@($details|Where-Object Comparable).Count;LogicalProcessors=$logical;AvailablePhysicalRAM_MB=$ram.AvailablePhysical/1MB;TotalPhysicalRAM_MB=$ram.TotalPhysical/1MB;CpuPackage_W=$cpuEnergy.Watts;CpuPackageInterval_Wh=$cpuEnergy.Wh;CpuPackageInterval_s=$cpuEnergy.Seconds;GpuNvidiaState=$gpu.State;GpuNvidia_W=$gpu.Watts;GpuNvidiaQueryAttempted=$gpu.QueryAttempted}
        $samples.Add($sample);$sample|Export-Csv -LiteralPath (Join-Path $run 'samples.csv') -Append -NoTypeInformation -Encoding utf8
        $powerText=if($null -ne $watts){'{0:N2} W batterie' -f $watts}else{'W PC inconnus (secteur/lecture indisponible)'}
        $cpuText=if($null -ne $totalCpu){'{0:N1} %' -f $totalCpu}else{'initialisation'}
        $leaders=($top|ForEach-Object {'{0}[{1}] {2:N1}%' -f $_.Name,$_.PID,$_.CPUPercentMachine}) -join ' ; '
        $packageText=if($cpuEnergy){'{0:N2} W' -f $cpuEnergy.Watts}else{'indisponible'}
        $chargeText=if($null -ne $sample.BatteryPercent){"$($sample.BatteryPercent) %"}else{'inconnue'}
        Write-Host ('{0:HH:mm:ss}  |  {1}  |  Processeur {2} · {3}  |  GPU NVIDIA {6}  |  Charge {4}  |  {5}' -f (Get-Date),$powerText,$packageText,$cpuText,$chargeText,$leaders,(Format-NvidiaState $gpu.State $gpu.Watts))
        if($sourceChanged){$stopReason='source-changed-window-invalid';break}
        if($source -eq 'Battery' -and $status.BatteryLifePercent -lt 15){$stopReason='battery-below-15-percent';break}
        if($firstSource -eq 'Unknown'){$stopReason='unknown-power-source';break}
        if($elapsed -ge ($SettleSeconds+$duration)){$completed=$true;$stopReason='completed';break}
        $previousCpu=$cpu;$previousProcesses=$current;$previousElapsed=$elapsed;$previousMeter=$meterReading
        $nextBoundary=if($elapsed -lt $SettleSeconds){$SettleSeconds}else{$SettleSeconds+$duration}
        $remaining=$nextBoundary-$clock.Elapsed.TotalSeconds
        if($remaining -gt 0){Start-Sleep -Milliseconds ([int][Math]::Min($IntervalSeconds*1000,[Math]::Ceiling($remaining*1000)))}
    }while($true)
}catch{$failure=$_.Exception.Message;$stopReason='error'}
finally{
    $window=@($samples|Where-Object InMeasurement)
    $gpuWatts=@($window | Where-Object { $null -ne $_.GpuNvidia_W } | Select-Object -ExpandProperty GpuNvidia_W)
    $gpuOff=@($window | Where-Object GpuNvidiaState -eq 'D3').Count
    $gpuStatus=if(!$window.Count){'Aucun relevé'}elseif($gpuOff -eq $window.Count){'OFF (veille)'}else{
        $lastGpu=$window[-1]
        (Format-NvidiaState $lastGpu.GpuNvidiaState $lastGpu.GpuNvidia_W)+' au dernier relevé'
    }
    $windowSeconds=if($window.Count -ge 2){$window[-1].Elapsed_s-$window[0].Elapsed_s}else{0.0}
    if($meter){$meter.Dispose()}
    $cpuPowerWindow=@($window | Where-Object {$_.CpuIntervalInMeasurement -and $null -ne $_.CpuPackage_W})
    $cpuPowerSeconds=($cpuPowerWindow|Measure-Object CpuPackageInterval_s -Sum).Sum
    $cpuEnergyWh=($cpuPowerWindow|Measure-Object CpuPackageInterval_Wh -Sum).Sum
    $cpuPowerCoverage=if($windowSeconds -gt 0){[Math]::Min(1,$cpuPowerSeconds/$windowSeconds)}else{0.0}
    $cpuStatus=if($meterIssue){$meterIssue}elseif($cpuPowerWindow.Count){'EMI Intel RAPL'}else{'Aucun intervalle valide'}
    $rates=@($window|Where-Object {$null -ne $_.BatteryDischarge_W}|Select-Object -ExpandProperty BatteryDischarge_W|Sort-Object)
    $rateCoverage=if($window.Count){[double]$rates.Count/$window.Count}else{0.0}
    $maxSampleGap=0.0
    for($i=1;$i -lt $window.Count;$i++){
        $maxSampleGap=[Math]::Max($maxSampleGap,$window[$i].Elapsed_s-$window[$i-1].Elapsed_s)
    }
    $sampleGapDetected=$maxSampleGap -gt [Math]::Max(5,2*$IntervalSeconds)
    $windowValid=$completed -and !$sampleGapDetected -and $stopReason -eq 'completed' -and !$sourceChanged -and $windowSeconds -gt 0 -and $firstSource -ne 'Unknown'
    $powerValid=$windowValid -and $firstSource -eq 'Battery' -and $rates.Count -ge 2 -and $rateCoverage -ge 0.9
    $mean=$null;$median=$null;$energy=$null;$coveredSeconds=0.0;$capacityDelta=$null;$capacitySlope=$null
    if($powerValid){
        $mean=($rates|Measure-Object -Average).Average;$middle=[int][Math]::Floor($rates.Count/2);$median=if($rates.Count%2){$rates[$middle]}else{($rates[$middle-1]+$rates[$middle])/2}
        $energy=0.0
        for($i=1;$i -lt $window.Count;$i++){if($null -ne $window[$i-1].BatteryDischarge_W -and $null -ne $window[$i].BatteryDischarge_W){$dt=$window[$i].Elapsed_s-$window[$i-1].Elapsed_s;$energy+=($window[$i-1].BatteryDischarge_W+$window[$i].BatteryDischarge_W)/2*$dt/3600;$coveredSeconds+=$dt}}
        if($coveredSeconds/$windowSeconds -lt 0.9){$powerValid=$false;$mean=$null;$median=$null;$energy=$null}
        else{$mean=$energy*3600/$coveredSeconds}
        if($window[0].NativeRemainingCapacity -gt 0 -and $window[-1].NativeRemainingCapacity -gt 0 -and $window[0].NativeRemainingCapacity -ne [uint32]::MaxValue -and $window[-1].NativeRemainingCapacity -ne [uint32]::MaxValue){$capacityDelta=($window[0].NativeRemainingCapacity-$window[-1].NativeRemainingCapacity)/1000.0;$capacitySlope=$capacityDelta*3600/$windowSeconds}
    }
    $cpuWindow=@($samples|Where-Object {$_.CpuIntervalInMeasurement -and $null -ne $_.TotalCPUPercent})
    $measuredWall=if($samples.Count){[Math]::Max(0,$samples[-1].Elapsed_s-$SettleSeconds)}else{0.0}
    $processSummary=@($byProcess.Values|ForEach-Object {[pscustomobject]@{PID=$_.PID;ProcessIdentity=$_.ProcessIdentity;Name=$_.Name;CPUSeconds=$_.CPUSeconds;ObservedInterval_s=$_.ObservedInterval_s;MeanCPUPercentMachineOverWindow=$(if($measuredWall -gt 0){100.0*$_.CPUSeconds/$measuredWall/$logical}else{$null});LastPrivateCommitted_MB=$_.LastPrivateCommitted_MB;LastResidentWorkingSet_MB=$_.LastResidentWorkingSet_MB}}|Sort-Object CPUSeconds -Descending)
    $nameSummary=@(foreach($name in $byName.Keys){[pscustomobject]@{Name=$name;CPUSeconds=$byName[$name];MeanCPUPercentMachineOverWindow=$(if($measuredWall -gt 0){100.0*$byName[$name]/$measuredWall/$logical}else{$null})}})|Sort-Object CPUSeconds -Descending
    if($processSummary.Count){$processSummary|Export-Csv -LiteralPath (Join-Path $run 'process-summary.csv') -NoTypeInformation -Encoding utf8}
    if($nameSummary.Count){$nameSummary|Export-Csv -LiteralPath (Join-Path $run 'name-summary.csv') -NoTypeInformation -Encoding utf8}
    $topMemory=@($lastDetails|Where-Object {$null -ne $_.ResidentWorkingSet_MB}|Sort-Object ResidentWorkingSet_MB -Descending|Select-Object -First 10 PID,Name,PrivateCommitted_MB,ResidentWorkingSet_MB,PrivateResidentWorkingSet_MB,ShareableResidentWorkingSet_MB,SharedCommit_MB)
    $summary=[ordered]@{GpuNvidiaStatus=$gpuStatus;GpuNvidiaOffSamples=$gpuOff;GpuNvidiaPowerSamples=$gpuWatts.Count;GpuNvidiaPassiveOnly=[bool]$GpuPassive;MeanGpuNvidiaActive_W=$(if($gpuWatts.Count){($gpuWatts|Measure-Object -Average).Average});PeakGpuNvidiaActive_W=$(if($gpuWatts.Count){($gpuWatts|Measure-Object -Maximum).Maximum});PowerScheme=$powerScheme;BrightnessPercent=$brightness;Conditions=$Conditions;LightMode=[bool]$Light;CpuPackageChannel=$meterChannel;CpuPackageStatus=$cpuStatus;CpuPackageCoverageFraction=$cpuPowerCoverage;CpuPackageCovered_s=$cpuPowerSeconds;CpuPackageComparisonValid=($windowValid -and $cpuPowerCoverage -ge 0.9);MeanCpuPackage_W=$(if($cpuPowerSeconds -gt 0){$cpuEnergyWh*3600/$cpuPowerSeconds});CpuPackageEnergy_Wh=$cpuEnergyWh;PeakCpuPackage_W=$(if($cpuPowerWindow.Count){($cpuPowerWindow|Measure-Object CpuPackage_W -Maximum).Maximum});UTC=[DateTime]::UtcNow.ToString('o');Phase=$Phase;StopReason=$stopReason;Completed=$completed;Error=$failure;WindowValid=$windowValid;MeasurementUse=$(if($duration -lt 300 -or $windowSeconds -lt 0.95*$duration){'screening-short-window'}else{'comparison-window'});BatteryPowerComparisonValid=$powerValid;Source=$firstSource;SourceChanged=$sourceChanged;LargestSampleGap_s=$maxSampleGap;SampleGapDetected=$sampleGapDetected;RequestedMeasurement_s=$duration;Settle_s=$SettleSeconds;MeasuredWindow_s=$windowSeconds;Samples=$samples.Count;MeasurementSamples=$window.Count;ValidRateSamples=$rates.Count;RateCoverageFraction=$rateCoverage;MeanBatteryDischarge_W=$mean;MedianBatteryDischarge_W=$median;IntegratedBatteryDischarge_Wh=$energy;IntegratedRateCovered_s=$coveredSeconds;ReportedCapacityDelta_Wh=$capacityDelta;ReportedCapacitySlope_W=$capacitySlope;CapacityUnitsIndependentlyVerified=$false;MeanTotalCPUPercent=$(if($cpuWindow.Count){($cpuWindow.TotalCPUPercent|Measure-Object -Average).Average}else{$null});LogicalProcessors=$logical;TopNames=@($nameSummary|Select-Object -First 10);TopMemory=$topMemory;Notes=@('On AC, whole-PC power is unknown; no per-process watt estimate.','Battery capacity units/design/full-charge/wear are not independently qualified.','CPU totals use GetSystemTimes, including processes born/exited between snapshots. Per-process deltas exclude unreadable/new/terminated processes and PID reuse.','Process working sets overlap; sums are not unique RAM. Private committed bytes differ from private resident bytes. Shareable resident bytes are working set minus private working set, not a unique physical-memory attribution.','EX2 private/shareable resident fields are only queried on Windows build 22631+. Else those fields remain blank.','On systems with more than 64 logical processors, GetSystemTimes can have primary-group scope; process percentages use all active logical processors.','This sampler has its own CPU cost; keep the same script, brightness and workload across comparisons.')}
    $summary|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8
    $report=Join-Path $run 'rapport.html'
    Write-PowerReport $summary $window $report
    Write-Host "Rapport : $report"
    if(!$NoOpen -and !$Light -and $completed -and $Phase -eq 'mesure'){
        try { Invoke-Item -LiteralPath $report -ErrorAction Stop }
        catch { Write-Warning "Ouverture automatique du rapport indisponible." }
    }
    Write-Host "Termine : $stopReason. Resultats : $run"
    if($powerValid){Write-Host ('Decharge moyenne {0:N2} W ; energie integree {1:N3} Wh sur {2:N1} s couverts.' -f $mean,$energy,$coveredSeconds)}
    if($nameSummary.Count){$nameSummary|Select-Object -First 10|Format-Table Name,CPUSeconds,MeanCPUPercentMachineOverWindow -AutoSize|Out-Host}
    if($topMemory.Count){Write-Host 'Top RAM au dernier releve : resident inclut des pages partageables ; les sommes ne sont pas une RAM unique.';$topMemory|Format-Table PID,Name,PrivateCommitted_MB,ResidentWorkingSet_MB,PrivateResidentWorkingSet_MB,ShareableResidentWorkingSet_MB -AutoSize|Out-Host}
}
if($failure){throw $failure}
if(!$summary.WindowValid){Write-Warning 'Fenetre incomplete/invalide : ne pas utiliser comme comparaison avant/apres.'}
