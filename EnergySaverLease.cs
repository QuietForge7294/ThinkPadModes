using System;using System.IO;using System.Runtime.InteropServices;using System.ComponentModel;using System.Web.Script.Serialization;using ThinkPadModesV3;
internal sealed class EnergySaverLease:IDisposable {
 [StructLayout(LayoutKind.Sequential)]struct Status{public byte Ac,Flags,Percent,Saver;public uint Life,Full;}
 [DllImport("kernel32.dll")]static extern bool GetSystemPowerStatus(out Status s);
 [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr p);
 [DllImport("powrprof.dll")]static extern uint PowerGetActiveScheme(IntPtr p,out IntPtr g);
 [DllImport("powrprof.dll")]static extern uint PowerReadACValueIndex(IntPtr p,ref Guid s,ref Guid g,ref Guid v,out uint x);
 [DllImport("powrprof.dll")]static extern uint PowerReadDCValueIndex(IntPtr p,ref Guid s,ref Guid g,ref Guid v,out uint x);
 [DllImport("powrprof.dll")]static extern uint PowerWriteACValueIndex(IntPtr p,ref Guid s,ref Guid g,ref Guid v,uint x);
 [DllImport("powrprof.dll")]static extern uint PowerWriteDCValueIndex(IntPtr p,ref Guid s,ref Guid g,ref Guid v,uint x);
 [DllImport("powrprof.dll")]static extern uint PowerSetActiveScheme(IntPtr p,ref Guid g);
 static readonly Guid Group=new Guid("de830923-a562-41af-a086-e3a2c6bad2da"),Setting=new Guid("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");readonly Guid scheme,ac,dc;readonly uint beforeAc,beforeDc;bool touched,disposed;
 static void Check(uint c){if(c!=0)throw new Win32Exception((int)c);}
 static Guid Active(){IntPtr p;Check(PowerGetActiveScheme(IntPtr.Zero,out p));try{return (Guid)Marshal.PtrToStructure(p,typeof(Guid));}finally{LocalFree(p);}}
 static uint Read(Guid s,bool a){Guid g=Group,v=Setting;uint x;Check(a?PowerReadACValueIndex(IntPtr.Zero,ref s,ref g,ref v,out x):PowerReadDCValueIndex(IntPtr.Zero,ref s,ref g,ref v,out x));return x;}
 static void Write(Guid s,bool a,uint x){Guid g=Group,v=Setting;Check(a?PowerWriteACValueIndex(IntPtr.Zero,ref s,ref g,ref v,x):PowerWriteDCValueIndex(IntPtr.Zero,ref s,ref g,ref v,x));if(Read(s,a)!=x)throw new IOException("Energy saver threshold not confirmed");}
 internal static bool ActiveSaver{get{Status s;if(!GetSystemPowerStatus(out s))throw new IOException("Energy saver status unavailable");return s.Saver==1;}}
 internal static void Refresh(){Guid s=Active(),ac=WindowsPower.Read(true),dc=WindowsPower.Read(false);Check(PowerSetActiveScheme(IntPtr.Zero,ref s));WindowsPower.Set(true,ac);WindowsPower.Set(false,dc);}
 internal EnergySaverLease(){scheme=Active();ac=WindowsPower.Read(true);dc=WindowsPower.Read(false);beforeAc=Read(scheme,true);beforeDc=Read(scheme,false);try{touched=true;Write(scheme,true,100);Write(scheme,false,100);Guid g=scheme;Check(PowerSetActiveScheme(IntPtr.Zero,ref g));WindowsPower.Set(true,ac);WindowsPower.Set(false,dc);}catch{Dispose();throw;}}
 public void Dispose(){if(disposed)return;try{if(touched){Guid nowAc=WindowsPower.Read(true),nowDc=WindowsPower.Read(false);if(Read(scheme,true)==100)Write(scheme,true,beforeAc);if(Read(scheme,false)==100)Write(scheme,false,beforeDc);if(Active()==scheme){Guid g=scheme;Check(PowerSetActiveScheme(IntPtr.Zero,ref g));WindowsPower.Set(true,nowAc);WindowsPower.Set(false,nowDc);}}}finally{disposed=true;}}
}
internal static class EnergySaverProbe {public static int Main(string[] args){if(args.Length!=1)return 64;bool before=EnergySaverLease.ActiveSaver,on=false;string error=null;try{using(var lease=new EnergySaverLease()){for(int n=0;n<15;n++){System.Threading.Thread.Sleep(500);if(EnergySaverLease.ActiveSaver){on=true;break;}}}}catch(Exception e){error=e.Message;}System.Threading.Thread.Sleep(1500);File.WriteAllText(args[0],new JavaScriptSerializer().Serialize(new{UTC=DateTime.UtcNow.ToString("o"),Before=before,EnabledObserved=on,After=EnergySaverLease.ActiveSaver,Error=error}));return error==null&&on?0:1;}}
