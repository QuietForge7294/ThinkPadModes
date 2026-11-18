using System;using System.IO;using System.Diagnostics;using System.Security.Principal;using System.Threading;using System.Windows.Forms;using ThinkPadModesV3;
internal static class ThinkPadManualApp {
 internal static bool StartupLaunch;
 internal static readonly string Root=@"C:\ProgramData\ThinkPadModes\Manual";internal static string Image{get{return typeof(ThinkPadManualApp).Assembly.Location;}}
 [STAThread] public static int Main(string[] a){try{if(a.Length==2&&a[0]=="--close-checks")return ModernChecks.CloseChecks(a[1]);if(a.Length==2&&a[0]=="--verify-zero-inputs"){var n=NvidiaSleepGuard.Open();double? cpu=null;string error=null;try{using(var c=new CalibrationCpu())cpu=c.Temperature();}catch(Exception e){var native=e as System.ComponentModel.Win32Exception;error=e.Message+(native==null?"":" (Windows "+native.NativeErrorCode+")");}Atomic.Write(a[1],new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{FoundNvidia=n!=null,RtxD3=n!=null&&n.Observe(CoolingPaths.Tick),CpuTemperature=cpu,CpuError=error}));return n!=null?0:1;}if(a.Length==2&&a[0]=="--gpu-verify")return GpuTemperatureChecks.Run(a[1]);if(a.Length==2&&a[0]=="--verify-firmware-auto"){
  try{
   LenovoDriverBootstrap.EnsureRunning();
   using(var ec=new LenovoOemTransport(true)){
    ec.SetBothOem(128);
    try{
     ec.SelectOem(false);int first=ec.Read(0x2F);
     ec.SelectOem(true);int second=ec.Read(0x2F);
     if(first!=128||second!=128)throw new IOException("Auto non relu sur les deux banques : "+first+"/"+second);
     Atomic.Write(a[1],"{\"AutoVerified\":true,\"First\":128,\"Second\":128}");
    }finally{ec.SelectOem(false);}
   }
   return 0;
  }catch(Exception error){
   Atomic.Write(a[1],new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{AutoVerified=false,Error=error.Message}));
   return 1;
  }
 }if(a.Length==1&&a[0]=="--verify")return ModernChecks.Verify();if(a.Length==2&&a[0]=="--render-checks")return ModernChecks.RenderChecks(Path.GetFullPath(a[1]));if(a.Length==4&&a[0]=="--modern-worker")return ModernWorker.Run(Int32.Parse(a[1]),a[2],a[3]);if(a.Length==5&&a[0]=="--modern-watch")return ModernWorker.Watch(Int32.Parse(a[1]),Int32.Parse(a[2]),a[3],a[4]);if(a.Length==2&&a[0]=="--qa-victim")return ModernChecks.Victim(a[1]);bool qa=a.Length==2&&a[0]=="--qa",closeCheck=a.Length==2&&(a[0]=="--close-and-open"||a[0]=="--smoke-close"),repair=a.Length==2&&a[0]=="--repair-verify";StartupLaunch=a.Length==1&&a[0]=="--startup";if(a.Length>0&&!qa&&!closeCheck&&!repair&&!StartupLaunch)return 64;
 if(!qa&&!closeCheck&&!repair&&ModernIntegration.RevealExisting())return 0;
 if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)){if(!qa&&!closeCheck&&!repair&&ModernIntegration.RunInstalled(Image)){for(int n=0;n<100;n++){if(ModernIntegration.RevealExisting())return 0;Thread.Sleep(100);}return 0;}Process.Start(new ProcessStartInfo(Image,(qa||closeCheck||repair)?a[0]+" \""+Path.GetFullPath(a[1])+"\"":StartupLaunch?"--startup":""){UseShellExecute=true,Verb="runas"});return 0;}
 foreach(string name in new[]{"ThinkPad-CPU","ThinkPadModesNativeUI","ThinkPadModesManual"})foreach(var p in Process.GetProcessesByName(name)){using(p){if(p.Id==Process.GetCurrentProcess().Id)continue;if(name=="ThinkPadModesNativeUI"&&p.MainModule.FileName.IndexOf("ThinkPad-Modes",StringComparison.OrdinalIgnoreCase)>=0){p.Kill();p.WaitForExit();continue;}if(p.MainWindowHandle!=IntPtr.Zero){p.CloseMainWindow();if(!p.WaitForExit(7000)){if(name=="ThinkPadModesNativeUI"||Digest.Of(p.MainModule.FileName)=="27284D3F7B7872CDCA65BD51E40ACD89E70C92A556F72D6768F0F85D1873F844"||Digest.Of(p.MainModule.FileName)=="B280D465DA59844BC776B55947C348B0D4EEBD02F6E585CC5691A32850158566"){p.Kill();p.WaitForExit();}else throw new IOException("Ferme l’ancienne fenêtre avant de continuer.");}}}}
 foreach(string name in new[]{"ThinkPad-CPU","ThinkPadModesManual"})foreach(var p in Process.GetProcessesByName(name)){using(p){if(p.Id!=Process.GetCurrentProcess().Id&&!p.WaitForExit(10000))throw new IOException("Une ancienne session est encore active.");}}
 Directory.CreateDirectory(Root);if(closeCheck)Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Application.EnableVisualStyles();RunWindow(qa?Path.GetFullPath(a[1]):null,closeCheck?Path.GetFullPath(a[1]):null,repair?Path.GetFullPath(a[1]):null);if(closeCheck&&a[0]=="--close-and-open")RunWindow(null);if(qa){ModernChecks.RecoveryCheck(Path.GetFullPath(a[1]));RunWindow(null);}return 0;
 }catch(Exception e){if(a.Length==1&&a[0]=="--verify")return 1;if(a.Length==2&&a[0]=="--close-checks"){File.WriteAllText(a[1],e.ToString());return 1;}if(a.Length==2&&a[0]=="--render-checks"){Directory.CreateDirectory(a[1]);File.WriteAllText(Path.Combine(a[1],"render-error.txt"),e.ToString());return 1;}MessageBox.Show(e.Message,"ThinkPad Modes",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}}
 static void RunWindow(string qa,string closeReport=null,string repairReport=null){bool fresh;using(var mutex=new Mutex(true,@"Global\ThinkPadModes.Calibration",out fresh)){if(!fresh)throw new IOException("Un autre essai matériel est actif.");string journal=Path.Combine(Root,"cpu-lease.json");if(File.Exists(journal))new ZeroPowerLease(journal,true);Application.Run(new ModernWindow(false,qa,closeReport,repairReport));}}
}
