using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using TimeoutException = System.TimeoutException;
using Microsoft.Win32;
namespace ThinkPadModesV3 {
public sealed class InstallationInfo {public string OwnerSid;public string Desktop;public string Startup;public string Root;public string Version;}
public sealed class Request {public int Version=1;public string Command;public string Value;}
public sealed class Reply {public bool Ok;public string Error;public Snapshot Snapshot;}
public sealed class Snapshot {
 public string Utc;public string FanUtc;public long Tick;public string PerformanceAc;public string PerformanceDc;public string Source;public string State="Qualification incomplete";public string Detail="Aucun niveau manuel autorise";
 public Dictionary<string,SensorValue> Sensors=new Dictionary<string,SensorValue>();public int? Fan1;public int? Fan2;public string FanStatus="Indisponible";public string Saved;public string Startup="OFF";public bool CanControl;public bool CanZero;
}
public static class Paths {
 public static string Data {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"ThinkPadModes");}}
 public static string Root {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"ThinkPadModes");}}
 public static InstallationInfo Info(){return new JavaScriptSerializer().Deserialize<InstallationInfo>(File.ReadAllText(Path.Combine(Root,"installation.json")));}
}
public static class PipeClient {
 public const string Name="ThinkPadModes.v1";
 [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle handle,out uint pid);
 internal static string ReadLine(Stream stream,int maximum,int timeout){var b=new List<byte>();byte[] buffer=new byte[1024];long deadline=Clock.Ms+timeout;while(b.Count<maximum){int remaining=(int)(deadline-Clock.Ms);if(remaining<=0)throw new TimeoutException("IPC deadline");var result=stream.BeginRead(buffer,0,Math.Min(buffer.Length,maximum-b.Count),null,null);if(!result.AsyncWaitHandle.WaitOne(remaining))throw new TimeoutException("IPC read timeout");int read;try{read=stream.EndRead(result);}finally{result.AsyncWaitHandle.Close();}if(read==0)throw new IOException("IPC closed");for(int i=0;i<read;i++){if(buffer[i]==10){if(i!=read-1)throw new InvalidDataException("Multiple IPC messages rejected");return Encoding.UTF8.GetString(b.ToArray());}b.Add(buffer[i]);}}throw new InvalidDataException("IPC message size exceeded");}
 internal static void WaitConnection(NamedPipeServerStream pipe,Func<bool> stopping,int timeout){var connect=pipe.BeginWaitForConnection(null,null);long deadline=Clock.Ms+timeout;while(!connect.AsyncWaitHandle.WaitOne(100)){if(stopping()||(timeout>0&&Clock.Ms>=deadline))throw new TimeoutException("IPC connection wait interrupted");}try{pipe.EndWaitForConnection(connect);}finally{connect.AsyncWaitHandle.Close();}}
 public static Reply Send(string command,string value=null){using(var pipe=new NamedPipeClientStream(".",Name,PipeDirection.InOut,PipeOptions.Asynchronous)){pipe.Connect(1000);uint pid;if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out pid)||pid!=ServiceNative.ProcessId())throw new UnauthorizedAccessException("IPC server is not the registered service process");byte[] request=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new Request {Command=command,Value=value})+"\n");pipe.Write(request,0,request.Length);pipe.Flush();return new JavaScriptSerializer().Deserialize<Reply>(ReadLine(pipe,65536,3000));}}
}
internal sealed class Engine:IDisposable {
 readonly object gate=new object();readonly SettingsStore store=new SettingsStore(Path.Combine(Paths.Data,"settings.json"));readonly InstallationInfo info;
 readonly Snapshot state=new Snapshot();volatile bool stopping;Thread sampler;Thread server;NamedPipeServerStream pending;bool ecStopped;
 public Engine(){info=Paths.Info();Settings saved=store.Load();state.Saved=store.LoadStatus;state.Detail=saved==null?"Enregistrer conserve les profils Windows actuels":"Reglages charges ; reprise materielle en attente de qualification";}
 public void Start(){sampler=new Thread(Sample){IsBackground=true};sampler.Start();server=new Thread(Serve){IsBackground=true};server.Start();}
 void Sample(){CpuTelemetry cpu=null;NvmlTelemetry gpu=null;EcBus ec=null;try{
  try{cpu=new CpuTelemetry();}catch(Exception e){lock(gate)state.Sensors["CpuPackage"]=SensorValue.Missing("C","LHM/PawnIO",e.GetBaseException().Message);}
  try{gpu=new NvmlTelemetry();}catch(Exception e){lock(gate)state.Sensors["GpuTemperature"]=SensorValue.Missing("C","NVML",e.GetBaseException().Message);}
  string quarantine=Path.Combine(Paths.Data,"ec-quarantine.json");if(File.Exists(quarantine)){ecStopped=true;lock(gate)state.FanStatus="EC suspendu apres defaut documente ; aucun reessai automatique";}else try{ec=new EcBus();}catch(Exception e){lock(gate)state.FanStatus=e.Message;ecStopped=true;Atomic.Write(quarantine,new JavaScriptSerializer().Serialize(new {UTC=DateTime.UtcNow.ToString("o"),Error=e.Message}));}
  while(!stopping){var sensors=new Dictionary<string,SensorValue>();
   if(cpu!=null)try{foreach(var pair in cpu.Read())sensors[pair.Key]=pair.Value;}catch(Exception e){sensors["CpuPackage"]=SensorValue.Missing("C","LHM/PawnIO",e.Message);}
   if(gpu!=null)try{foreach(var pair in gpu.Read())sensors[pair.Key]=pair.Value;}catch(Exception e){sensors["GpuTemperature"]=SensorValue.Missing("C","NVML",e.Message);}
   FanSample fan=null;string fanError=null;if(ec!=null&&!ecStopped)try{fan=FanAcquisition.Read(ec,false);}catch(Exception e){fanError=e.Message;ecStopped=true;Atomic.Write(quarantine,new JavaScriptSerializer().Serialize(new {UTC=DateTime.UtcNow.ToString("o"),Error=e.Message}));}
   lock(gate){state.Utc=DateTime.UtcNow.ToString("o");state.Tick=Clock.Ms;foreach(var pair in sensors)state.Sensors[pair.Key]=pair.Value;if(fan!=null){state.Fan1=fan.Rpm1;state.Fan2=fan.Rpm2;state.FanUtc=fan.AcquisitionUtc;state.FanStatus="Canal EC selectionne uniquement ; comparaison Lenovo non validee";}if(fanError!=null){state.Fan1=null;state.Fan2=null;state.FanUtc=null;state.FanStatus=fanError+" ; acquisition EC suspendue";}ReadPower();}
   for(int i=0;i<20&&!stopping;i++)Thread.Sleep(100);
  }
 }catch(Exception e){lock(gate){state.State="Defaut";state.Detail=e.GetBaseException().Message;}}finally{if(ec!=null)ec.Dispose();if(cpu!=null)cpu.Dispose();if(gpu!=null)gpu.Dispose();}}
 void ReadPower(){try{state.PerformanceAc=ModeName(WindowsPower.Read(true));state.PerformanceDc=ModeName(WindowsPower.Read(false));state.Source=WindowsPower.OnMains()?"AC":"DC";}catch(Exception e){state.Detail=e.Message;}}
 static string ModeName(Guid id){return id==WindowsPower.Eco?"Eco":id==WindowsPower.Turbo?"Turbo":id==Guid.Empty?"Normal":"Custom";}
 static Guid ModeId(string s){if(s=="Eco")return WindowsPower.Eco;if(s=="Normal")return Guid.Empty;if(s=="Turbo")return WindowsPower.Turbo;throw new ArgumentException("Mode inconnu");}
 void ResumeSaved(){Settings saved=store.Load();state.Saved=store.LoadStatus;if(saved==null){state.Detail="Aucun reglage enregistre a reprendre";return;}using(var key=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS")){string bios=Convert.ToString(key.GetValue("BIOSVersion"));if(Convert.ToString(key.GetValue("SystemProductName"))!=saved.Model||bios!=saved.Bios||Convert.ToString(key.GetValue("ECFirmwareMajorRelease"))+"."+Convert.ToString(key.GetValue("ECFirmwareMinorRelease"))!=saved.EcVersion)throw new InvalidOperationException("Identite/BIOS/EC modifies ; reprise bloquee");}
  foreach(string name in new[]{"CpuPackage","GpuTemperature"}){SensorValue v;if(!state.Sensors.TryGetValue(name,out v)||v.Status!="valid"||!v.Value.HasValue||Clock.Ms-v.MonotonicMs>6000)throw new InvalidOperationException("Reprise en attente de capteurs CPU/GPU frais");}
  Guid beforeAc=WindowsPower.Read(true),beforeDc=WindowsPower.Read(false);Guid ac=ModeId(saved.PerformanceAc),dc=ModeId(saved.PerformanceDc);try{WindowsPower.Set(true,ac);if(WindowsPower.Read(false)!=beforeDc)throw new InvalidOperationException("Profil batterie modifie entre-temps");WindowsPower.Set(false,dc);}catch{if(WindowsPower.Read(true)==ac)WindowsPower.Set(true,beforeAc);if(WindowsPower.Read(false)==dc)WindowsPower.Set(false,beforeDc);throw;}ReadPower();state.Detail="Profils Windows enregistres repris ; ventilation conservee au firmware";
 }
 Reply Handle(Request request){if(request.Version!=1)return new Reply {Error="IPC version mismatch"};lock(gate){try{
  switch(request.Command){
   case "status":ReadPower();state.Startup=StartupIntegration.Status(info);break;
   case "performance":WindowsPower.Set(WindowsPower.OnMains(),ModeId(request.Value));ReadPower();break;
   case "resume":ResumeSaved();break;
   case "diagnose-ec":string diagnostic=Path.Combine(Paths.Data,"ec-instrumented.json");if(File.Exists(diagnostic))throw new InvalidOperationException("Diagnostic deja execute ; examiner la preuve avant toute reprise");Atomic.Write(diagnostic,"{\"Pending\":true}");ecStopped=true;var result=new Dictionary<string,object>{{"UTC",DateTime.UtcNow.ToString("o")},{"FanModeWritesPermitted",false}};EcBus probe=null;try{probe=new EcBus(1000);result["Fan"]=FanAcquisition.Read(probe,false);}catch(Exception e){result["Error"]=e.GetBaseException().Message;}finally{if(probe!=null){result["Trace"]=probe.Trace;probe.Dispose();}Atomic.Write(diagnostic,new JavaScriptSerializer().Serialize(result));}break;
   case "save":ReadPower();var saved=new Settings {PerformanceAc=state.PerformanceAc,PerformanceDc=state.PerformanceDc,Cooling="Auto",ZeroDb=false};store.Save(saved);state.Saved=store.LoadStatus;break;
   case "startup":if(request.Value!="ON"&&request.Value!="OFF")throw new ArgumentException("Invalid startup state");StartupIntegration.Set(info,request.Value=="ON");state.Startup=StartupIntegration.Status(info);break;
   case "auto":state.State="Controle firmware non modifie";state.Detail="Aucune consigne manuelle n'a ete appliquee par cette version";break;
   default:throw new InvalidOperationException("Commande absente de la qualification actuelle");
  }
  return new Reply {Ok=true,Snapshot=Clone(state)};
 }catch(Exception e){return new Reply {Ok=false,Error=e.GetBaseException().Message,Snapshot=Clone(state)};}}}
 static Snapshot Clone(Snapshot value){var j=new JavaScriptSerializer();return j.Deserialize<Snapshot>(j.Serialize(value));}
 void Serve(){while(!stopping){try{
  var acl=new PipeSecurity();acl.SetAccessRuleProtection(true,false);acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-5-2"),PipeAccessRights.FullControl,AccessControlType.Deny));acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(info.OwnerSid),PipeAccessRights.ReadWrite,AccessControlType.Allow));foreach(string sid in new[]{"S-1-5-18","S-1-5-32-544"})acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid),PipeAccessRights.FullControl,AccessControlType.Allow));
  using(var pipe=new NamedPipeServerStream(PipeClient.Name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,acl)){pending=pipe;PipeClient.WaitConnection(pipe,()=>stopping,0);if(stopping)return;
   string text=PipeClient.ReadLine(pipe,8192,1000);var serializer=new JavaScriptSerializer();var raw=serializer.DeserializeObject(text) as Dictionary<string,object>;if(raw==null||raw.Keys.Any(k=>k!="Version"&&k!="Command"&&k!="Value"))throw new InvalidDataException("IPC schema mismatch");Reply reply=Handle(serializer.Deserialize<Request>(text));byte[] output=Encoding.UTF8.GetBytes(serializer.Serialize(reply)+"\n");pipe.Write(output,0,output.Length);pipe.Flush();pending=null;}
 }catch(Exception e){if(stopping)return;try{Atomic.Write(Path.Combine(Paths.Data,"ipc-last-error.json"),new JavaScriptSerializer().Serialize(new {UTC=DateTime.UtcNow.ToString("o"),Error=e.GetBaseException().Message,Type=e.GetBaseException().GetType().Name}));}catch{}Thread.Sleep(100);}}}
 public void Dispose(){stopping=true;if(pending!=null)try{pending.Dispose();}catch{}if(server!=null)server.Join(2000);if(sampler!=null)sampler.Join(3000);}
}
internal sealed class EngineService:ServiceBase {
 Engine engine;public EngineService(){ServiceName="ThinkPadModesEngine";CanStop=true;CanShutdown=true;}
 protected override void OnStart(string[] args){engine=new Engine();engine.Start();}
 protected override void OnStop(){if(engine!=null){engine.Dispose();engine=null;}}
 protected override void OnShutdown(){OnStop();}
}
}
