using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
// Coupled nonzero laboratory test. Tachometers and mode reports come from LSDK.
// Direct actuator uses the exact pinned OEM bank selector writes0/1 and mode2F.
internal sealed class LenovoSdkLabSession:ILenovoSession {
 readonly MemoryMappedFile memory;readonly MemoryMappedViewAccessor view;readonly Process guardian;
 readonly Stopwatch timer=Stopwatch.StartNew();bool touched,restored,confirmedAuto;int samples,level;long started;
 public static bool Requested {get{return !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TPM_LAB_GUARD_NAME"));}}
 static long Tick {get{return (long)NativeTick();}}
 [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="GetTickCount64")]static extern ulong NativeTick();
 public LenovoSdkLabSession(){string name=Environment.GetEnvironmentVariable("TPM_LAB_GUARD_NAME");int pid=Int32.Parse(Environment.GetEnvironmentVariable("TPM_LAB_GUARD_PID"));guardian=Process.GetProcessById(pid);if(ThinkPadModesV3.Digest.Of(guardian.MainModule.FileName)!=ThinkPadModesV3.Digest.Of(typeof(LenovoSdkLabSession).Assembly.Location))throw new IOException("Guardian executable differs");memory=MemoryMappedFile.OpenExisting(name);view=memory.CreateViewAccessor();level=(int)view.ReadInt64(24);if(level!=1&&level!=7)throw new IOException("Unsupported banked lab level");Check();view.Write(32,(long)Process.GetCurrentProcess().Id);Pulse();}
 void Check(){long age=Tick-view.ReadInt64(8);if(guardian.HasExited||age<0||age>1500)throw new IOException("Independent guardian unavailable");}
 public void Busy(bool busy){view.Write(64,busy?1L:0L);}
 public void Pulse(){Check();view.Write(0,Tick);}
 public void Sample(object frame){Pulse();var d=(Dictionary<string,object>)frame;int mode=Convert.ToInt32(d["Mode"]);if(mode!=(touched&&!restored?level:128))throw new IOException("SDK mode differs from requested banked mode");if(Convert.ToInt32(d["Fan1"])<=0||Convert.ToInt32(d["Fan2"])<=0)throw new IOException("Nonpositive native tachometer");samples++;if(restored&&mode==128)confirmedAuto=true;
  if(samples>=2&&!touched){double cpu=view.ReadDouble(40),gpu=view.ReadDouble(48),watts=view.ReadDouble(56);if(cpu>=65||gpu>=60||watts>=15)return;view.Write(16,2L);touched=true;using(var ec=new LenovoOemTransport(true))ec.SetBothOem((byte)level);started=timer.ElapsedMilliseconds;Pulse();}
  else if(touched&&!restored&&timer.ElapsedMilliseconds-started>=(level==1?20000:10000)){using(var ec=new LenovoOemTransport(true))ec.SetBothOem(0x80);restored=true;view.Write(16,0L);Pulse();}
 }
 public bool Continuous {get{return false;}}
 public bool Completed {get{return touched&&restored&&confirmedAuto&&samples>=4;}}
 public void Dispose(){try{if(touched){using(var ec=new LenovoOemTransport(true))ec.SetBothOem(0x80);view.Write(16,0L);}}finally{view.Dispose();memory.Dispose();guardian.Dispose();}}
}
