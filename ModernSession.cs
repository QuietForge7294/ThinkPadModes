using System;using System.IO;using System.IO.MemoryMappedFiles;using System.Security.AccessControl;using System.Security.Principal;using System.Text;using System.Diagnostics;using System.Threading;using System.Threading.Tasks;using System.Globalization;using System.Collections.Generic;using System.Web.Script.Serialization;using ThinkPadModesV3;
internal sealed class ModeFrame {
 public long Sequence,Tick,FanTick,Frames,Rejected;public double? Cpu,Watts,Gpu;public int? Fan1,Fan2,Mode;public int Target,Requested=128,Performance,ZeroFrames,Cadence;public bool CapVerified;public bool? Saver;public string Error;
 internal static string ModeName(int? mode){return !mode.HasValue?"Indisponible":mode==0?"0 dB":mode==128?"Auto Lenovo":mode==1?"Bas":mode==7?"Renforcé":"Inconnu";}
 internal static int Period(bool active,bool test){return test?500:active?2000:30000;}
 internal bool Fresh{get{return FanTick>0&&CoolingPaths.Tick-FanTick>=0&&CoolingPaths.Tick-FanTick<=Math.Max(3000,Cadence*2);}}
}
internal sealed class ModeChannel:IDisposable {
 internal readonly MemoryMappedFile Memory;internal readonly MemoryMappedViewAccessor View;internal readonly EventWaitHandle Wake;internal readonly string Name;
 static MemoryMappedFileSecurity Security(){var s=new MemoryMappedFileSecurity();s.SetAccessRuleProtection(true,false);foreach(string sid in new[]{"S-1-5-18","S-1-5-32-544"})s.AddAccessRule(new AccessRule<MemoryMappedFileRights>(new SecurityIdentifier(sid),MemoryMappedFileRights.FullControl,AccessControlType.Allow));return s;}
 internal ModeChannel(string name,bool create,bool test=false){Name=name;if(test){Memory=MemoryMappedFile.CreateNew(null,4096);Wake=new EventWaitHandle(false,EventResetMode.AutoReset);}else if(create){Memory=MemoryMappedFile.CreateNew(name,4096,MemoryMappedFileAccess.ReadWrite,MemoryMappedFileOptions.None,Security(),HandleInheritability.None);var security=new EventWaitHandleSecurity();security.SetAccessRuleProtection(true,false);foreach(string sid in new[]{"S-1-5-18","S-1-5-32-544"})security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(sid),EventWaitHandleRights.FullControl,AccessControlType.Allow));bool fresh;Wake=new EventWaitHandle(false,EventResetMode.AutoReset,name+".wake",out fresh,security);}else{Memory=MemoryMappedFile.OpenExisting(name);Wake=EventWaitHandle.OpenExisting(name+".wake");}View=Memory.CreateViewAccessor();if(create){View.Write(0,0x544D4F4445530001L);View.Write(8,2000);View.Write(20,15);}}
 internal void Text(int offset,int capacity,string value){byte[] bytes=Encoding.UTF8.GetBytes(value??"");int n=Math.Min(bytes.Length,capacity-4);View.WriteArray(offset+4,bytes,0,n);View.Write(offset,n);}
 internal string Text(int offset,int capacity){int n=View.ReadInt32(offset);if(n<0||n>capacity-4)throw new IOException("Invalid shared text length");var bytes=new byte[n];View.ReadArray(offset+4,bytes,0,n);return Encoding.UTF8.GetString(bytes);}
 internal ModeFrame Read(){for(int tries=0;tries<4;tries++){long seq=View.ReadInt64(32);if(seq==0||(seq&1)!=0)continue;var f=new ModeFrame{Sequence=seq,Tick=View.ReadInt64(40),Cpu=Number(48),Watts=Number(56),Gpu=Number(64),FanTick=View.ReadInt64(72),Fan1=Positive(80),Fan2=Positive(84),Mode=Positive(88),Target=View.ReadInt32(92),CapVerified=View.ReadInt32(96)==1,Saver=View.ReadInt32(100)<0?(bool?)null:View.ReadInt32(100)==1,Requested=View.ReadInt32(104),Performance=View.ReadInt32(108),ZeroFrames=View.ReadInt32(112),Error=Text(116,512),Frames=View.ReadInt64(1200),Rejected=View.ReadInt64(1208),Cadence=View.ReadInt32(1216)};Thread.MemoryBarrier();if(seq==View.ReadInt64(32))return f;}return null;}
 int? Positive(int o){int x=View.ReadInt32(o);return x<0?(int?)null:x;}
 double? Number(int o){double x=View.ReadDouble(o);return Double.IsNaN(x)||Double.IsInfinity(x)?(double?)null:x;}
 internal void Publish(ModeFrame f){long next=(View.ReadInt64(32)&~1L)+2;View.Write(32,next-1);Thread.MemoryBarrier();View.Write(40,f.Tick);View.Write(48,f.Cpu??Double.NaN);View.Write(56,f.Watts??Double.NaN);View.Write(64,f.Gpu??Double.NaN);View.Write(72,f.FanTick);View.Write(80,f.Fan1??-1);View.Write(84,f.Fan2??-1);View.Write(88,f.Mode??-1);View.Write(92,f.Target);View.Write(96,f.CapVerified?1:0);View.Write(100,f.Saver.HasValue?(f.Saver.Value?1:0):-1);View.Write(104,f.Requested);View.Write(108,f.Performance);View.Write(112,f.ZeroFrames);Text(116,512,f.Error);View.Write(1200,f.Frames);View.Write(1208,f.Rejected);View.Write(1216,f.Cadence);Thread.MemoryBarrier();View.Write(32,next);}
 public void Dispose(){View.Dispose();Memory.Dispose();Wake.Dispose();}
}
internal sealed class ModernSession:IDisposable {
 internal readonly ModeChannel Channel;internal readonly string Folder;internal readonly Process Reader;readonly Process watcher;readonly SemaphoreSlim commands=new SemaphoreSlim(1,1);readonly object lifecycle=new object();long command;bool stopped,disposed;Task stopTask;
 internal ModernSession(bool test){if(!test)throw new ArgumentException();Channel=new ModeChannel("",true,true);Folder="";}
 internal ModernSession(){Folder=Path.Combine(ThinkPadManualApp.Root,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));Directory.CreateDirectory(Folder);Channel=new ModeChannel("Local\\ThinkPadModes.Modern."+Guid.NewGuid().ToString("N"),true);Reader=Process.Start(new ProcessStartInfo(ThinkPadManualApp.Image,"--modern-worker "+Process.GetCurrentProcess().Id+" \""+Channel.Name+"\" \""+Folder+"\""){UseShellExecute=false,CreateNoWindow=true});try{watcher=Process.Start(new ProcessStartInfo(ThinkPadManualApp.Image,"--modern-watch "+Process.GetCurrentProcess().Id+" "+Reader.Id+" \""+Channel.Name+"\" \""+Folder+"\""){UseShellExecute=false,CreateNoWindow=true});}catch{Channel.View.Write(12,1);Channel.Wake.Set();Reader.WaitForExit(6000);Channel.Dispose();throw;}}
 internal void Cadence(int milliseconds,bool immediate){lock(lifecycle){if(stopped||disposed)return;Channel.View.Write(8,milliseconds);if(immediate)Channel.View.Write(16,Channel.View.ReadInt32(16)+1);Channel.Wake.Set();}}
 internal async Task<string> Command(int op,int arg,int preferred){await commands.WaitAsync();try{lock(lifecycle){if(stopped||disposed)throw new OperationCanceledException("Fermeture en cours");}if(Reader.HasExited)throw new IOException("Le lecteur matériel est arrêté");Channel.View.Write(20,preferred);long n=++command;Channel.View.Write(648,op);Channel.View.Write(652,arg);Thread.MemoryBarrier();Channel.View.Write(640,n);Channel.Wake.Set();long begin=CoolingPaths.Tick;while(true){lock(lifecycle){if(stopped||disposed)throw new OperationCanceledException("Fermeture en cours");if(Channel.View.ReadInt64(656)==n)break;}if(Reader.HasExited)throw new IOException("Le lecteur matériel s’est arrêté");if(CoolingPaths.Tick-begin>20000)throw new IOException("Commande en attente : aucune nouvelle commande envoyée");await Task.Delay(40);}string message=Channel.Text(668,512);if(Channel.View.ReadInt32(664)!=1)throw new IOException(message);return message;}finally{commands.Release();}}
 internal int WatcherId{get{return watcher.Id;}}
 internal Task Stop(){lock(lifecycle){if(stopTask!=null)return stopTask;if(disposed)return Task.FromResult(0);stopped=true;Channel.View.Write(12,1);Channel.Wake.Set();stopTask=Reader==null?Task.FromResult(0):Task.Run(()=>{if(!Reader.WaitForExit(7000))throw new IOException("Le lecteur termine encore la restauration");if(!watcher.WaitForExit(7000))throw new IOException("Le processus de restauration termine encore sa vérification");});return stopTask;}}
 public void Dispose(){lock(lifecycle){if(disposed)return;disposed=true;stopped=true;Channel.Dispose();if(Reader!=null)Reader.Dispose();if(watcher!=null)watcher.Dispose();}}
}
internal sealed class ModernWorker {
 [System.Runtime.InteropServices.DllImport("kernel32.dll")]static extern uint WaitForMultipleObjects(uint count,IntPtr[] handles,bool all,uint timeout);
 readonly ModeChannel channel;readonly string folder,journal;readonly ModeFrame frame=new ModeFrame();CalibrationCpu cpu;PawnModule msr;NvmlTelemetry gpu;ZeroPowerLease lease;StreamWriter csv;double unit,energyBefore;long powerTick,lastFlush;bool paused;string cpuUnavailable;double? previousEmiWh;long previousEmiTick;EmiMeter cpuMeter;NvidiaSleepGuard nvidia;int preferred=15;long readCount,flushCount;readonly Stopwatch clock=Stopwatch.StartNew();
 internal ModernWorker(ModeChannel c,string f){channel=c;folder=f;journal=Path.Combine(ThinkPadManualApp.Root,"cpu-lease.json");}
 internal static void Auto(){ForcedZeroReader.EmergencyAuto(0);}
 internal static int Run(int parentId,string name,string folder){try{using(var parent=Process.GetProcessById(parentId)){if(Digest.Of(parent.MainModule.FileName)!=Digest.Of(ThinkPadManualApp.Image))return 77;using(var c=new ModeChannel(name,false)){if(c.View.ReadInt64(0)!=0x544D4F4445530001L)return 77;return new ModernWorker(c,folder).Loop(parent);}}}catch(Exception e){Atomic.Write(Path.Combine(folder,"worker-error.json"),new JavaScriptSerializer().Serialize(new{UTC=DateTime.UtcNow.ToString("o"),Error=e.Message}));return 1;}}
 int Loop(Process parent){IDisposable pins=null;string failure=null;bool restored=false;try{pins=LenovoOemTransport.PinForSession();LenovoDriverBootstrap.EnsureRunning();paused=PauseLegacyCooling();Auto();OpenCpuSensors();nvidia=NvidiaSleepGuard.Open();csv=new StreamWriter(Path.Combine(folder,"mesures.csv"),false,Encoding.UTF8,16384);csv.WriteLine("utc;cap_W;cpu_C;cpu_W;gpu_C;fan1_rpm;fan2_rpm;mode_demande;mode_lu;rpm_age_ms;cap_verifie;economiseur_windows;cadence_ms");channel.View.Write(24,1);long next=0,lastCommand=0;int immediate=channel.View.ReadInt32(16);
 while(!parent.HasExited&&channel.View.ReadInt32(12)==0){long cmd=channel.View.ReadInt64(640);if(cmd>lastCommand){string text;bool ok;try{preferred=Cap(channel.View.ReadInt32(20));text=Execute(channel.View.ReadInt32(648),channel.View.ReadInt32(652));ok=true;}catch(Exception e){text=e.Message;ok=false;}lastCommand=cmd;int commandCadence=channel.View.ReadInt32(8);frame.Cadence=commandCadence==500||commandCadence==30000?commandCadence:2000;Sample();next=CoolingPaths.Tick+(GuardedCadence(frame.Requested,frame.Cadence));Flush();channel.Text(668,512,text);channel.View.Write(664,ok?1:0);Thread.MemoryBarrier();channel.View.Write(656,cmd);}int cadence=channel.View.ReadInt32(8);if(cadence!=500&&cadence!=2000&&cadence!=30000)cadence=2000;frame.Cadence=cadence;int request=channel.View.ReadInt32(16);if(request!=immediate){immediate=request;next=0;}cadence=GuardedCadence(frame.Requested,cadence);frame.Cadence=cadence;long now=CoolingPaths.Tick;if(now>=next){long began=now;Sample();channel.View.Write(1224,Math.Max(channel.View.ReadInt64(1224),CoolingPaths.Tick-began));next=CoolingPaths.Tick+cadence;}int delay=(int)Math.Max(1,Math.Min(30000,next-CoolingPaths.Tick));channel.Wake.WaitOne(delay);}
 }catch(Exception e){failure=e.Message;frame.Error=e.Message;channel.Publish(frame);}finally{try{Auto();restored=true;}catch(Exception e){failure=(failure??"")+" ; Auto : "+e.Message;}try{Release();}catch(Exception e){failure=(failure??"")+" ; CPU : "+e.Message;}try{Flush();if(csv!=null)csv.Dispose();}catch(Exception e){failure=(failure??"")+" ; CSV : "+e.Message;}if(cpu!=null)cpu.Dispose();if(msr!=null)msr.Dispose();if(cpuMeter!=null)cpuMeter.Dispose();try{ReleaseGpu();}catch(Exception e){failure=(failure??"")+" ; NVML : "+e.Message;}try{if(paused){var r=CoolingClient.Send("resume-acquisition");if(!r.Ok)failure=(failure??"")+" ; acquisition : "+r.Error;}}catch(Exception e){failure=(failure??"")+" ; acquisition : "+e.Message;}if(pins!=null)pins.Dispose();Atomic.Write(Path.Combine(folder,"session-result.json"),new JavaScriptSerializer().Serialize(new{UTC=DateTime.UtcNow.ToString("o"),Error=failure,AutomaticRestored=restored,CpuLeaseRestored=!File.Exists(journal),Frames=frame.Frames,Rejected=frame.Rejected,ReadCount=readCount,FlushCount=flushCount,PeriodicJsonWrites=0}));}return restored&&!File.Exists(journal)?0:1;}
 static bool PauseLegacyCooling(){
  using(var service=new System.ServiceProcess.ServiceController(CoolingPaths.Service)){
   var status=service.Status;
   if(status==System.ServiceProcess.ServiceControllerStatus.Stopped)return false;
   if(status!=System.ServiceProcess.ServiceControllerStatus.Running)throw new IOException("Le service de ventilation historique change d’état.");
   var reply=CoolingClient.Send("pause");
   if(!reply.Ok)throw new IOException(reply.Error);
   return true;
  }
 }
 internal static bool GpuNeeded(int requested,bool test){return requested!=0&&(requested!=128||test);}
 internal static int GuardedCadence(int requested,int cadence){return requested==0?Math.Min(1000,cadence):requested==128?cadence:Math.Min(2000,cadence);}
 internal static bool ZeroSafe(double? cpu,bool sleeping,bool capped,bool entering){
  return sleeping&&capped&&cpu.HasValue&&cpu.Value>=0&&cpu.Value<(entering?65:70);
 }
 void ValidateZero(){
  if(cpu==null)throw new IOException("0 dB indisponible : "+cpuUnavailable);
  int cadence=channel.View.ReadInt32(8);
  if(cadence==500)throw new IOException("Désactive Test avant de demander le 0 dB.");
  if(frame.Requested==0)return;
  if(frame.Requested!=128||cadence!=2000)throw new IOException("Sélectionne Auto Lenovo et laisse cette fenêtre au premier plan.");
  long now=CoolingPaths.Tick;
  if(nvidia==null)throw new IOException("0 dB : la RTX n’a pas été détectée par Windows.");
  if(!nvidia.Observe(now))throw new IOException("0 dB : la RTX est active ou Windows ne confirme pas sa veille.");
  int remaining=nvidia.SecondsRemaining(now);
  if(remaining>0)throw new IOException(String.Format("0 dB : encore {0} s de veille RTX en Auto Lenovo.",remaining));
  if(!ZeroSafe(cpu.Temperature(),true,true,true))
   throw new IOException("0 dB : température CPU absente ou au-dessus de la limite.");
 }
 internal static bool ThermalSafe(int requested,double? cpu,double? gpu){
  return cpu.HasValue&&gpu.HasValue&&cpu.Value>=0&&gpu.Value>=0&&cpu.Value<(requested==7?80:75)&&gpu.Value<72;
 }
 double? ReadGpu(){if(gpu==null)gpu=new NvmlTelemetry();return gpu.TemperatureOnly();}
 void ReleaseGpu(){var old=gpu;gpu=null;if(old!=null)old.Dispose();}
 void OpenCpuSensors(){
  try{
   cpu=new CalibrationCpu();
   var info=Paths.Info();
   msr=new PawnModule("IntelMSR.bin",Path.Combine(Paths.Root,"versions",info.Version,"modules"));
   unit=Math.Pow(.5,(int)((msr.Execute("ioctl_read_msr",0x606)>>8)&31));
  }catch(Exception error){
   if(cpu!=null){cpu.Dispose();cpu=null;}
   if(msr!=null){msr.Dispose();msr=null;}
   var native=error as System.ComponentModel.Win32Exception;
   cpuUnavailable=error.Message+(native==null?"":" (Windows "+native.NativeErrorCode+")");
   try{cpuMeter=EmiMeter.Open();}catch(Exception){}
  }
 }
 void SampleCpu(){
  if(cpu!=null){
   frame.Cpu=cpu.Temperature();
   double energy=(msr.Execute("ioctl_read_msr",0x611)&0xffffffffUL)*unit;
   long now=CoolingPaths.Tick;
   if(powerTick>0&&now>powerTick)
    frame.Watts=(energy>=energyBefore?energy-energyBefore:energy+4294967296.0*unit-energyBefore)*1000/(now-powerTick);
   energyBefore=energy;powerTick=now;
  }else if(cpuMeter!=null){
   var reading=cpuMeter.Sample();
   double wh=reading[0]/1e12;
   long tick=(long)reading[1];
   if(previousEmiWh.HasValue&&wh>=previousEmiWh.Value&&tick>previousEmiTick)
    frame.Watts=(wh-previousEmiWh.Value)*3600e7/(tick-previousEmiTick);
   previousEmiWh=wh;previousEmiTick=tick;
  }
 }
 internal static int Cap(int w){if(w!=10&&w!=15&&w!=20)throw new ArgumentException("Plafond autorisé : 10, 15 ou 20 W");return w;}
 void Apply(int w){if(cpu==null)throw new IOException("Plafond CPU indisponible : "+cpuUnavailable);Release();lease=new ZeroPowerLease(journal,false,Cap(w));frame.Target=w;frame.Performance=1;}
 void Release(){if(lease!=null){lease.Dispose();lease=null;}frame.Target=0;frame.CapVerified=false;try{var mode=WindowsPower.Read(WindowsPower.OnMains());frame.Performance=mode==WindowsPower.Eco?1:mode==WindowsPower.Turbo?3:2;}catch{frame.Performance=0;}}
 string Execute(int op,int arg){if(op==1){if(arg!=0&&arg!=1&&arg!=7&&arg!=128)throw new ArgumentException("Mode inconnu");if(arg==0){ValidateZero();ReleaseGpu();}else if(arg!=128){if(cpu==null)throw new IOException("Mode manuel indisponible : "+cpuUnavailable);double? temperature=ReadGpu();if(!temperature.HasValue||!ThermalSafe(arg,cpu.Temperature(),temperature))throw new IOException("Capteurs thermiques indisponibles ou au-dessus de la limite ; mode manuel refusé.");}if(arg==0){if(lease==null)Apply(preferred);if(!lease.Verified)throw new IOException("0 dB : plafond CPU non confirmé.");if(!nvidia.Observe(CoolingPaths.Tick))throw new IOException("La RTX est sortie de veille ; Auto Lenovo conservé.");WindowsPower.Set(true,WindowsPower.Eco);WindowsPower.Set(false,WindowsPower.Eco);frame.Performance=1;}using(var ec=new LenovoOemTransport(true,arg==0))ec.SetBothOem((byte)arg);frame.Requested=arg;frame.ZeroFrames=0;return ModeFrame.ModeName(arg)+" demandé · confirmation par les RPM";}if(op==2){Apply(arg);return arg+" W appliqués et relus · Turbo Boost désactivé";}if(op==3){if(frame.Requested==0)Execute(1,128);Release();return "Réglages CPU précédents restaurés";}if(op==4){if(arg==1){if(cpu==null){bool onMains=WindowsPower.OnMains();WindowsPower.Set(onMains,WindowsPower.Eco);if(WindowsPower.Read(onMains)!=WindowsPower.Eco)throw new IOException("Profil Windows non confirmé");frame.Performance=1;return "Éco Windows appliqué, plafond CPU indisponible";}Apply(preferred);return "Éco · "+preferred+" W · Turbo Boost désactivé";}if(arg!=2&&arg!=3)throw new ArgumentException();if(frame.Requested==0)Execute(1,128);Release();bool mains=WindowsPower.OnMains();Guid mode=arg==2?Guid.Empty:WindowsPower.Turbo;WindowsPower.Set(mains,mode);if(WindowsPower.Read(mains)!=mode)throw new IOException("Profil Windows non confirmé");frame.Performance=arg;return "Profil "+(arg==2?"Normal":"Turbo")+" appliqué";}if(op==5){Atomic.Write(Path.Combine(ThinkPadManualApp.Root,"settings.json"),new JavaScriptSerializer().Serialize(new{CpuWatts=preferred,StartupFan="Auto",Manual=true}));return "Plafond préféré enregistré · démarrage en Auto Lenovo";}throw new ArgumentException("Commande inconnue");}
 void Sample(){frame.Tick=CoolingPaths.Tick;frame.Error=null;frame.Cpu=frame.Gpu=frame.Watts=null;if(cpuUnavailable!=null)frame.Error="Température CPU et plafonds indisponibles : "+cpuUnavailable;bool rtxSleeping=false;if(nvidia!=null){if(frame.Requested==0||(frame.Requested==128&&channel.View.ReadInt32(8)==2000))rtxSleeping=nvidia.Observe(frame.Tick);else nvidia.Reset();}try{SampleCpu();if(GpuNeeded(frame.Requested,channel.View.ReadInt32(8)==500)){frame.Gpu=ReadGpu();if(!frame.Gpu.HasValue)frame.Error=gpu.TemperatureError;}else ReleaseGpu();}catch(Exception e){frame.Error=e.Message;}
 if(frame.Requested==0&&!ZeroSafe(frame.Cpu,rtxSleeping,lease!=null&&lease.Verified,false)){
  Auto();frame.Requested=128;frame.ZeroFrames=0;
  frame.Error=rtxSleeping?"0 dB : surveillance CPU indisponible ou seuil atteint. Retour à Auto Lenovo.":"0 dB : la RTX est active ou son état est inconnu. Retour à Auto Lenovo.";
 }else if(frame.Requested!=128&&frame.Requested!=0&&!ThermalSafe(frame.Requested,frame.Cpu,frame.Gpu)){
  Auto();frame.Requested=128;frame.ZeroFrames=0;ReleaseGpu();frame.Error="Protection thermique : retour à Auto Lenovo.";
 }
 bool valid=false;int a=-1,b=-1,m=-1,n=-1;for(int retry=0;retry<3&&!valid;retry++){try{using(var ec=new LenovoOemTransport(false)){try{valid=DirectRpmSession.Bank(ec,false,out a,out m)&&DirectRpmSession.Bank(ec,true,out b,out n)&&m==n;}finally{ec.SelectOem(false);}}}catch(Exception e){frame.Error=e.Message;}if(!valid){frame.Rejected++;if(retry<2)Thread.Sleep(40);}}
 if(valid){frame.Fan1=a;frame.Fan2=b;frame.Mode=m;frame.FanTick=CoolingPaths.Tick;frame.Frames++;frame.ZeroFrames=a==0&&b==0&&m==0?frame.ZeroFrames+1:0;}else{frame.ZeroFrames=0;frame.Fan1=frame.Fan2=frame.Mode=null;frame.Error=frame.Error??"Lecture RPM incohérente rejetée · consigne conservée";}
 try{frame.Saver=EnergySaverLease.ActiveSaver;}catch{frame.Saver=null;}try{frame.CapVerified=lease!=null&&lease.Verified;}catch{frame.CapVerified=false;}readCount++;channel.View.Write(1232,readCount);channel.Publish(frame);
 try{csv.WriteLine(DateTime.UtcNow.ToString("o")+";"+frame.Target+";"+N(frame.Cpu)+";"+N(frame.Watts)+";"+N(frame.Gpu)+";"+frame.Fan1+";"+frame.Fan2+";"+frame.Requested+";"+frame.Mode+";"+(valid?(CoolingPaths.Tick-frame.FanTick).ToString():"")+";"+frame.CapVerified+";"+frame.Saver+";"+frame.Cadence);if(CoolingPaths.Tick-lastFlush>=5000||frame.Cadence==30000)Flush();}catch(Exception e){frame.Error="Enregistrement : "+e.Message;channel.Publish(frame);}}
 void Flush(){if(csv==null)return;csv.Flush();lastFlush=CoolingPaths.Tick;flushCount++;channel.View.Write(1240,flushCount);}
 static string N(double? n){return n.HasValue?n.Value.ToString("F3",CultureInfo.InvariantCulture):"";}
 internal static int Watch(int parentId,int workerId,string name,string folder){string error=null;bool auto=false,cpu=false;try{using(var parent=Process.GetProcessById(parentId))using(var worker=Process.GetProcessById(workerId))using(var channel=new ModeChannel(name,false)){string hash=Digest.Of(ThinkPadManualApp.Image);if(Digest.Of(parent.MainModule.FileName)!=hash||Digest.Of(worker.MainModule.FileName)!=hash)return 77;WaitForMultipleObjects(2,new[]{parent.Handle,worker.Handle},false,0xffffffff);if(worker.HasExited&&File.Exists(Path.Combine(folder,"session-result.json"))){var result=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(folder,"session-result.json")));if(Convert.ToBoolean(result["AutomaticRestored"])&&Convert.ToBoolean(result["CpuLeaseRestored"])){Atomic.Write(Path.Combine(folder,"recovery-result.json"),new JavaScriptSerializer().Serialize(new{UTC=DateTime.UtcNow.ToString("o"),AutomaticRestored=true,CpuLeaseRestored=true,Error=(string)null,NormalClosure=true}));return 0;}}channel.View.Write(12,1);channel.Wake.Set();if(!worker.WaitForExit(7000))ForcedZeroReader.EmergencyAuto(workerId);Auto();auto=true;new ZeroPowerLease(Path.Combine(ThinkPadManualApp.Root,"cpu-lease.json"),true);cpu=!File.Exists(Path.Combine(ThinkPadManualApp.Root,"cpu-lease.json"));try{using(var service=new System.ServiceProcess.ServiceController(CoolingPaths.Service))if(service.Status==System.ServiceProcess.ServiceControllerStatus.Running)CoolingClient.Send("resume-acquisition");}catch{}}}catch(ArgumentException){try{Auto();auto=true;new ZeroPowerLease(Path.Combine(ThinkPadManualApp.Root,"cpu-lease.json"),true);cpu=!File.Exists(Path.Combine(ThinkPadManualApp.Root,"cpu-lease.json"));}catch(Exception e){error=e.Message;}}catch(Exception e){error=e.Message;}Atomic.Write(Path.Combine(folder,"recovery-result.json"),new JavaScriptSerializer().Serialize(new{UTC=DateTime.UtcNow.ToString("o"),AutomaticRestored=auto,CpuLeaseRestored=cpu,Error=error}));return auto&&cpu?0:1;}
}
