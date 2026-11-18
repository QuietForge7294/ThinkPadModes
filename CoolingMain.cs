using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Web.Script.Serialization;
using ThinkPadModesV3;
internal static class CoolingMain {
 static bool SystemAccount {get{return WindowsIdentity.GetCurrent().IsSystem;}}
 public static int Main(string[] args){
  if(args.Length==1&&args[0]=="--service"){if(!SystemAccount)return 77;ServiceBase.Run(new CoolingService());return 0;}
  if(args.Length==4&&args[0]=="--sdk-colocate-service"){if(!SystemAccount)return 77;return LenovoSdkProbe.Main(args);}
  if(args.Length==2&&args[0]=="--read"){if(!SystemAccount||Environment.GetEnvironmentVariable("TPM_COOLING_SERVICE")!="1")return 77;return LenovoSdkProbe.Main(args);}
  if(args.Length==2&&args[0]=="--setup")return CoolingSetup.Run(Path.GetFullPath(args[1]));
  if(args.Length==2&&args[0]=="--elevate-install"){string report=Path.GetFullPath(args[1]);try{var p=Process.Start(new ProcessStartInfo(typeof(CoolingMain).Assembly.Location,"--setup \""+report+"\""){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden});p.WaitForExit();Atomic.Write(report+".launcher.json",new JavaScriptSerializer().Serialize(new{UTC=DateTime.UtcNow.ToString("o"),ExitCode=p.ExitCode}));return p.ExitCode;}catch(Exception e){Atomic.Write(report+".launcher.json",new JavaScriptSerializer().Serialize(new{Error=e.Message}));return 1;}}
  if(args.Length==2&&args[0]=="--verify-recordings")return LenovoLabVerification.Run(args[1]);
  if(args.Length>=2){string command=null,value=null,report=null;if(args.Length==2){report=args[1];if(args[0]=="--status")command="status";else if(args[0]=="--save")command="save";else if(args[0]=="--resume")command="resume";else if(args[0]=="--pause")command="pause";else if(args[0]=="--resume-acquisition")command="resume-acquisition";else if(args[0]=="--test-telemetry-drop")command="test-telemetry-drop";else if(args[0]=="--requalify")command="requalify";else if(args[0]=="--test-worker-stop")command="test-worker-stop";else if(args[0]=="--test-service-crash")command="test-service-crash";}else if(args.Length==3&&(args[0]=="--set"||args[0]=="--startup")){command=args[0]=="--set"?"set":"startup";value=args[1];report=args[2];}
   if(command!=null){try{CoolingReply result=CoolingClient.Send(command,value);Atomic.Write(report,new JavaScriptSerializer().Serialize(result));return result.Ok?0:1;}catch(Exception e){Atomic.Write(report,new JavaScriptSerializer().Serialize(new{Ok=false,Error=e.Message}));return 1;}}
  }return 64;
 }
}
