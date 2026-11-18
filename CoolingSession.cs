using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using ThinkPadModesV3;
internal interface ILenovoSession:IDisposable {
 void Pulse();void Busy(bool value);void Sample(object frame);bool Completed{get;}bool Continuous{get;}
}
internal sealed class CoolingSession:ILenovoSession {
 readonly MemoryMappedFile memory;readonly MemoryMappedViewAccessor view;readonly Process guardian;
 public bool RestoredAutomatic {get;private set;}
 int applied=128,goodFrames,badFrames;bool owned;long coolSince=-1;
 public bool Completed {get{return false;}}public bool Continuous {get{return true;}}
 public CoolingSession(){guardian=Process.GetProcessById(Int32.Parse(Environment.GetEnvironmentVariable("TPM_LAB_GUARD_PID")));if(Digest.Of(guardian.MainModule.FileName)!=Digest.Of(typeof(CoolingSession).Assembly.Location))throw new IOException("Gardien de ventilation différent");memory=MemoryMappedFile.OpenExisting(Environment.GetEnvironmentVariable("TPM_LAB_GUARD_NAME"));view=memory.CreateViewAccessor();view.Write(32,(long)Process.GetCurrentProcess().Id);Pulse();}
 void Check(){long age=CoolingPaths.Tick-view.ReadInt64(8);if(guardian.HasExited||age<0||age>1500)throw new IOException("Gardien indépendant absent");}
 public void Pulse(){Check();if(view.ReadInt64(128)!=0){Apply(128);throw new OperationCanceledException("Arrêt contrôlé");}view.Write(0,CoolingPaths.Tick);}
 public void Busy(bool value){view.Write(64,value?1L:0L);}
 void Apply(int mode){if(mode==applied&&!owned)return;view.Write(16,2L);owned=true;using(var ec=new LenovoOemTransport(true))ec.SetBothOem((byte)mode);applied=mode;view.Write(80,mode);if(mode==128)view.Write(16,0L);}
 public bool TakeTestDrop(){if(view.ReadInt64(168)==0)return false;view.Write(168,0L);return true;}
 public void InvalidSample(string reason){badFrames++;goodFrames=0;coolSince=-1;view.Write(160,0L);view.Write(152,1L);view.Write(16,2L);owned=true;using(var ec=new LenovoOemTransport(true))ec.SetBothOem(0x80);applied=128;view.Write(80,128L);view.Write(112,128L);view.Write(16,0L);view.Write(104,0L);Pulse();if(badFrames>=3)throw new IOException("Trois mesures SDK consécutives incohérentes : "+reason);}
 public void Sample(object frame){Pulse();var d=(Dictionary<string,object>)frame;int actual=Convert.ToInt32(d["Mode"]),a=Convert.ToInt32(d["Fan1"]),b=Convert.ToInt32(d["Fan2"]);if(actual!=applied){InvalidSample("Le firmware a modifié le mode : "+actual);return;}badFrames=0;goodFrames++;view.Write(160,goodFrames>=3?1L:0L);
  view.Write(88,a);view.Write(92,b);view.Write(96,CoolingPaths.Tick);view.Write(104,view.ReadInt64(104)+1);view.Write(112,actual);
  int requested=(int)view.ReadInt64(24);if(requested!=128&&requested!=1&&requested!=7)throw new IOException("Consigne hors qualification");
  bool valid=view.ReadInt64(120)==1&&goodFrames>=3;double cpu=view.ReadDouble(40),gpu=view.ReadDouble(48),watts=view.ReadDouble(56);valid=valid&&!Double.IsNaN(cpu)&&!Double.IsNaN(gpu)&&!Double.IsNaN(watts)&&cpu>=0&&gpu>=0&&watts>=0;
  int next=requested;if(requested!=128){
   if(!valid||a<=0||b<=0||gpu>=72||cpu>=(requested==1?75:80)||(requested==1&&watts>=20)){next=128;coolSince=-1;}
   else if(applied==128){if(cpu>=65||gpu>=60||watts>=15){next=128;coolSince=-1;}else{if(coolSince<0)coolSince=CoolingPaths.Tick;if(CoolingPaths.Tick-coolSince<10000)next=128;}}
  }else coolSince=-1;
  if(next!=applied)Apply(next);Pulse();
 }
 public void Dispose(){try{if(owned){using(var ec=new LenovoOemTransport(true))ec.SetBothOem(0x80);view.Write(16,0L);view.Write(80,128L);RestoredAutomatic=true;}else RestoredAutomatic=true;}finally{try{if(guardian.HasExited){string journal=System.IO.Path.Combine(CoolingPaths.Data,"zero-cpu-lease.json");if(System.IO.File.Exists(journal))new ZeroPowerLease(journal,true);}}finally{view.Dispose();memory.Dispose();guardian.Dispose();}}}
}
