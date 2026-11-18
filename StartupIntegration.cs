using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Security.AccessControl;
using Microsoft.Win32;
namespace ThinkPadModesV3 {
public static class StartupIntegration {
 public const string Shortcut="ThinkPad Modes.lnk";
 static string Link(InstallationInfo info){return Path.Combine(info.Startup,Shortcut);}
 public static string Status(InstallationInfo info){bool link=File.Exists(Link(info));bool auto;using(var service=new ServiceController("ThinkPadModesEngine")){auto=service.StartType==ServiceStartMode.Automatic;}
  bool disabled=false;using(var key=Registry.Users.OpenSubKey(info.OwnerSid+@"\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder")){byte[] value=key==null?null:key.GetValue(Shortcut) as byte[];disabled=value!=null&&value.Length>0&&(value[0]&1)!=0;}
  return link&&auto&&!disabled?"ON":!link&&!auto?"OFF":disabled?"Desactive dans Windows":"Partiel";
 }
 public static void Set(InstallationInfo info,bool enabled){string link=Link(info);byte[] old=File.Exists(link)?File.ReadAllBytes(link):null;ServiceStartMode previous;using(var s=new ServiceController("ThinkPadModesEngine"))previous=s.StartType;
  try{if(enabled){CreateShortcut(Path.Combine(info.Root,"ThinkPadModesLauncher.exe"),link,"--tray");ServiceNative.SetStart("ThinkPadModesEngine",ServiceStartMode.Automatic);}else{if(File.Exists(link))File.Delete(link);ServiceNative.SetStart("ThinkPadModesEngine",ServiceStartMode.Manual);}string actual=Status(info);if(actual!=(enabled?"ON":"OFF"))throw new IOException("Etat reel du demarrage: "+actual);}
  catch{ServiceNative.SetStart("ThinkPadModesEngine",previous);if(old==null){if(File.Exists(link))File.Delete(link);}else File.WriteAllBytes(link,old);throw;}
 }
 public static void CreateShortcut(string target,string destination,string arguments){Directory.CreateDirectory(Path.GetDirectoryName(destination));Type type=Type.GetTypeFromProgID("WScript.Shell",true);object shell=Activator.CreateInstance(type);object shortcut=null;try{shortcut=type.InvokeMember("CreateShortcut",System.Reflection.BindingFlags.InvokeMethod,null,shell,new object[]{destination});Type t=shortcut.GetType();t.InvokeMember("TargetPath",System.Reflection.BindingFlags.SetProperty,null,shortcut,new object[]{target});t.InvokeMember("Arguments",System.Reflection.BindingFlags.SetProperty,null,shortcut,new object[]{arguments});t.InvokeMember("WorkingDirectory",System.Reflection.BindingFlags.SetProperty,null,shortcut,new object[]{Path.GetDirectoryName(target)});t.InvokeMember("Description",System.Reflection.BindingFlags.SetProperty,null,shortcut,new object[]{"ThinkPad Modes"});t.InvokeMember("Save",System.Reflection.BindingFlags.InvokeMethod,null,shortcut,null);}finally{if(shortcut!=null)Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell);}}
}
public static class ServiceNative {
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManager(string machine,string database,uint access);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenService(IntPtr manager,string name,uint access);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ChangeServiceConfig(IntPtr service,uint type,uint start,uint error,string binary,string group,IntPtr tag,string dependencies,string account,string password,string display);
 [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateService(IntPtr manager,string name,string display,uint access,uint type,uint start,uint error,string binary,string group,IntPtr tag,string dependencies,string account,string password);
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool DeleteService(IntPtr service);
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool SetServiceObjectSecurity(IntPtr service,uint information,byte[] descriptor);
 [StructLayout(LayoutKind.Sequential)]struct ServiceStatus {public uint Type,State,Controls,Win32Code,SpecificCode,Checkpoint,WaitHint,Pid,Flags;}
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out ServiceStatus status,int size,out int needed);
 public static uint ProcessId(){IntPtr manager=OpenSCManager(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{IntPtr service=OpenService(manager,"ThinkPadModesEngine",4);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{ServiceStatus status;int needed;if(!QueryServiceStatusEx(service,0,out status,Marshal.SizeOf(typeof(ServiceStatus)),out needed))throw new Win32Exception(Marshal.GetLastWin32Error());return status.Pid;}finally{CloseServiceHandle(service);}}finally{CloseServiceHandle(manager);}}
 static void Allowed(string name){if(name!="ThinkPadModesEngine")throw new ArgumentException("Service not allowed");}
 public static void SetStart(string name,ServiceStartMode start){Allowed(name);IntPtr manager=OpenSCManager(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{IntPtr service=OpenService(manager,name,2);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{if(!ChangeServiceConfig(service,0xFFFFFFFF,(uint)start,0xFFFFFFFF,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception(Marshal.GetLastWin32Error());}finally{CloseServiceHandle(service);}}finally{CloseServiceHandle(manager);}}
 public static void Install(string binary,string ownerSid){IntPtr manager=OpenSCManager(null,null,3);if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{IntPtr service=CreateService(manager,"ThinkPadModesEngine","ThinkPad Modes - capteurs et reglages",0xF01FF,0x10,3,1,"\""+binary+"\" --service",null,IntPtr.Zero,null,"LocalSystem",null);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{var acl=new RawSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x20095;;;"+ownerSid+")");byte[] bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);if(!SetServiceObjectSecurity(service,4,bytes))throw new Win32Exception(Marshal.GetLastWin32Error());}catch{DeleteService(service);throw;}finally{CloseServiceHandle(service);}}finally{CloseServiceHandle(manager);}}
 public static void Remove(){IntPtr manager=OpenSCManager(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{IntPtr service=OpenService(manager,"ThinkPadModesEngine",0x10000);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{if(!DeleteService(service))throw new Win32Exception(Marshal.GetLastWin32Error());}finally{CloseServiceHandle(service);}}finally{CloseServiceHandle(manager);}}
}
}
