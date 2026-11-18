$ErrorActionPreference='Stop'
$fx=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
Push-Location $PSScriptRoot
try {
$refs=@('/r:Microsoft.CSharp.dll','/r:System.Management.dll','/r:System.ServiceProcess.dll','/r:System.Web.Extensions.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll')
$refs+=@('UIAutomationClient','UIAutomationTypes','WindowsBase')|ForEach-Object {'/r:'+(Join-Path $fx "WPF\$_.dll")}
$sources=Get-ChildItem -File -Filter '*.cs'|Select-Object -ExpandProperty Name
& "$fx\csc.exe" /nologo /codepage:65001 /platform:x64 /target:winexe /win32manifest:Modern.manifest /main:ThinkPadManualApp /out:ThinkPadModesManual.exe /resource:assets\p14s-badge.png,P14sBadge.png /resource:assets\app-icon.ico,AppIcon.ico /win32icon:assets\app-icon.ico @refs @sources
if($LASTEXITCODE -ne 0){throw 'Compilation échouée'}
$p=Start-Process -FilePath .\ThinkPadModesManual.exe -ArgumentList '--verify' -PassThru -Wait
if($p.ExitCode -ne 0){throw ('Test RPM échoué: '+$p.ExitCode)}
Get-FileHash ThinkPadModesManual.exe
}finally{Pop-Location}
