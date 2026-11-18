using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using ThinkPadModesV3;
internal static class CoolingPaths {
 internal const string Service="ThinkPadModesCooling",Pipe="ThinkPadModes.Cooling.v1";
 internal const string Evidence="C89C526B3F1AAB0D5E79EB94746F8739CC58854DF289F0FF639D53F746DE16E7";
 internal static string Data {get{return Path.Combine(Paths.Data,"Cooling");}}
 internal static string InfoFile {get{return Path.Combine(Paths.Root,"cooling-installation.json");}}
 internal static CoolingInstallation Info(){return new JavaScriptSerializer().Deserialize<CoolingInstallation>(File.ReadAllText(InfoFile));}
 internal static long Tick {get{return (long)GetTickCount64();}}
 [DllImport("kernel32.dll")]static extern ulong GetTickCount64();
 internal static int Mode(string mode){if(mode=="Auto"||mode=="Zero")return 128;if(mode=="Bas")return 1;if(mode=="Renforce")return 7;throw new ArgumentException("Mode de ventilation inconnu");}
 internal static string Mode(int value){return value==128?"Auto":value==1?"Bas":value==7?"Renforce":"Inconnu";}
}
internal sealed class CoolingInstallation {public string OwnerSid,Directory,ExecutableSHA256,UiSHA256,Version;}
internal sealed class CoolingSettings {public int Version=1;public string Mode="Auto";public string Evidence=CoolingPaths.Evidence;}
internal sealed class CoolingSnapshot {
 public string UTC,Request="Auto",Actual="Auto",State="Initialisation",Detail="Lecture native Lenovo en attente",FanUTC,Saved="non enregistré",Startup="OFF";
 public int? Fan1,Fan2;public double? Cpu,Gpu,CpuWatts;public long FrameAgeMs;public bool CanControl,CanZero,Faulted,Pending,ZeroActive,ZeroMeasured;
}
internal sealed class CoolingRequest {public int Version=1;public string Command,Value;}
internal sealed class CoolingReply {public bool Ok;public string Error;public CoolingSnapshot Snapshot;}
internal static class CoolingClient {
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle h,out uint pid);
 internal static CoolingReply Send(string command,string value=null){using(var pipe=new NamedPipeClientStream(".",CoolingPaths.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous)){pipe.Connect(1500);uint pid;if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out pid)||pid!=CoolingNative.Pid())throw new UnauthorizedAccessException("Le serveur n'est pas le service de ventilation enregistré");byte[] b=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new CoolingRequest{Command=command,Value=value})+"\n");pipe.Write(b,0,b.Length);pipe.Flush();return new JavaScriptSerializer().Deserialize<CoolingReply>(PipeClient.ReadLine(pipe,65536,8000));}}
}
internal static class CoolingNative {
 [StructLayout(LayoutKind.Sequential)]struct ServiceStatus{public uint Type,State,Controls,Win32,Specific,Checkpoint,WaitHint,ProcessId,Flags;}
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManager(string machine,string db,uint access);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenService(IntPtr manager,string name,uint access);
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out ServiceStatus status,int length,out int needed);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryServiceConfig(IntPtr service,IntPtr data,int size,out int needed);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ChangeServiceConfig(IntPtr service,uint type,uint start,uint error,string binary,string group,IntPtr tag,string dependencies,string user,string password,string display);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateService(IntPtr manager,string name,string display,uint access,uint type,uint start,uint error,string binary,string group,IntPtr tag,string dependencies,string account,string password);
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool StartService(IntPtr service,int count,IntPtr args);
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool DeleteService(IntPtr service);
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool CloseServiceHandle(IntPtr handle);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text,uint revision,out IntPtr descriptor,out uint length);
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool SetServiceObjectSecurity(IntPtr service,uint info,IntPtr descriptor);
 [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr memory);
 static T With<T>(uint access,Func<IntPtr,T> action){IntPtr m=OpenSCManager(null,null,1);if(m==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{IntPtr s=OpenService(m,CoolingPaths.Service,access);if(s==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{return action(s);}finally{CloseServiceHandle(s);}}finally{CloseServiceHandle(m);}}
 internal static uint Pid(){return With(4,s=>{ServiceStatus v;int n;if(!QueryServiceStatusEx(s,0,out v,Marshal.SizeOf(typeof(ServiceStatus)),out n))throw new Win32Exception(Marshal.GetLastWin32Error());return v.ProcessId;});}
 internal static bool Automatic(){return With(1,s=>{int n;QueryServiceConfig(s,IntPtr.Zero,0,out n);if(n<12||n>65536)throw new IOException("Taille configuration de service invalide");IntPtr data=Marshal.AllocHGlobal(n);try{if(!QueryServiceConfig(s,data,n,out n))throw new Win32Exception(Marshal.GetLastWin32Error());return Marshal.ReadInt32(data,4)==2;}finally{Marshal.FreeHGlobal(data);}});}
 internal static void SetAutomatic(bool enabled){With(2,s=>{if(!ChangeServiceConfig(s,0xffffffff,enabled?2u:3u,0xffffffff,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception(Marshal.GetLastWin32Error());return 0;});if(Automatic()!=enabled)throw new IOException("Démarrage du service non confirmé");}
 internal static void Start(){With(0x10,s=>{if(!StartService(s,0,IntPtr.Zero)){int e=Marshal.GetLastWin32Error();if(e!=1056)throw new Win32Exception(e);}return 0;});}
 internal static void Remove(){With(0x10000,s=>{if(!DeleteService(s))throw new Win32Exception(Marshal.GetLastWin32Error());return 0;});}
 internal static void ChangeBinary(string binary){CoolingInstallation info=CoolingPaths.Info();if(Path.GetFullPath(binary)!=Path.Combine(info.Directory,"ThinkPadModesCooling.exe")||Digest.Of(binary)!=info.ExecutableSHA256)throw new IOException("Image de service non vérifiée");With(2,s=>{if(!ChangeServiceConfig(s,0xffffffff,0xffffffff,0xffffffff,"\""+binary+"\" --service",null,IntPtr.Zero,null,null,null,null))throw new Win32Exception(Marshal.GetLastWin32Error());return 0;});}
 internal static void Install(string binary,string ownerSid,bool automatic){IntPtr m=OpenSCManager(null,null,3);if(m==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{IntPtr s=CreateService(m,CoolingPaths.Service,"ThinkPad Modes — ventilation Lenovo",0xF01FF,0x10,automatic?2u:3u,1,"\""+binary+"\" --service",null,IntPtr.Zero,"ThinkPadModesEngine\0\0",null,null);if(s==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{IntPtr sd;uint length;string acl="D:P(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCRPWPLO;;;"+ownerSid+")";if(!ConvertStringSecurityDescriptorToSecurityDescriptor(acl,1,out sd,out length))throw new Win32Exception(Marshal.GetLastWin32Error());try{if(!SetServiceObjectSecurity(s,4,sd))throw new Win32Exception(Marshal.GetLastWin32Error());}finally{LocalFree(sd);}}catch{DeleteService(s);throw;}finally{CloseServiceHandle(s);}}finally{CloseServiceHandle(m);}}
}
