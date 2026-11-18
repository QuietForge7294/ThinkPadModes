using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;
using ThinkPadModesV3;
// One finite elevated batch. No service, scheduled task, arbitrary command IPC,
// shell execution, updated payload loading, or persistent elevation mechanism.
internal static class LenovoLabSuite {
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
 static string Image {get{return typeof(LenovoLabSuite).Assembly.Location;}}
 static bool Admin {get{return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}}
 static int[] LastLevel(string file,int level){int[] last=null;var root=(Dictionary<string,object>)Json.DeserializeObject(File.ReadAllText(file));foreach(var item in (object[])root["Samples"]){var row=(Dictionary<string,object>)item;if(Convert.ToInt32(row["Mode"])==level)last=new[]{Convert.ToInt32(row["Fan1"]),Convert.ToInt32(row["Fan2"])};}if(last==null)throw new IOException("Native requested-level frame missing");return last;}
 public static int Main(string[] args){
  if(args.Length==2&&args[0]=="--elevate-suite"){string report=Path.GetFullPath(args[1]);try{var p=Process.Start(new ProcessStartInfo(Image,"--prepare-suite \""+report+"\""){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden});p.WaitForExit();Atomic.Write(report+".launcher.json",Json.Serialize(new{UTC=DateTime.UtcNow.ToString("o"),ExitCode=p.ExitCode}));return p.ExitCode;}catch(Exception e){Atomic.Write(report+".launcher.json",Json.Serialize(new{UTC=DateTime.UtcNow.ToString("o"),Error=e.Message}));return 1;}}
  if(!Admin)return 77;
  if(args.Length==2&&args[0]=="--prepare-suite"){
   string report=Path.GetFullPath(args[1]);if(File.Exists(report))return 75;try{
   string directory=Path.Combine(Paths.Data,"Lab",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
   var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);var administrators=new SecurityIdentifier("S-1-5-32-544");acl.SetOwner(administrators);
   foreach(string sid in new[]{"S-1-5-18","S-1-5-32-544"})acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));Directory.SetAccessControl(directory,acl);
   string target=Path.Combine(directory,"ThinkPadModesLab.exe"),digest=Digest.Of(Image);File.Copy(Image,target,false);if(Digest.Of(target)!=digest)throw new IOException("Protected batch digest differs");
   Atomic.Write(report,Json.Serialize(new{UTC=DateTime.UtcNow.ToString("o"),Pending=true,ProtectedDirectory=directory,ExecutableSHA256=digest}));var p=Process.Start(new ProcessStartInfo(target,"--suite \""+directory+"\""){UseShellExecute=false,CreateNoWindow=true});p.WaitForExit();Atomic.Write(report,Json.Serialize(new{UTC=DateTime.UtcNow.ToString("o"),ProtectedDirectory=directory,ExecutableSHA256=digest,SuiteExitCode=p.ExitCode,Success=p.ExitCode==0}));return p.ExitCode;}catch(Exception e){Atomic.Write(report,Json.Serialize(new{UTC=DateTime.UtcNow.ToString("o"),Success=false,Error=e.Message}));return 1;}
  }
  if(args.Length!=2||args[0]!="--suite")return 64;
  string root=Path.GetFullPath(args[1]);if(Path.GetDirectoryName(Image)!=root)throw new IOException("Batch must run from its protected directory");
  string stateFile=Path.Combine(root,"suite-result.json");if(File.Exists(stateFile))return 75;
  var jobs=new List<object>();var result=new Dictionary<string,object>{{"UTC",DateTime.UtcNow.ToString("o")},{"Jobs",jobs},{"StopAllowed",false},{"ProductionQualified",false}};
  try{
   foreach(var item in new[]{new[]{"banked7","--guard-sdk"},new[]{"banked7-crash","--guard-sdk-crash"},new[]{"auto-after-crash","--colocate"},new[]{"banked1","--guard-sdk-low"},new[]{"auto-final","--colocate"}}){
    string report=Path.Combine(root,item[0]+".json");Atomic.Write(stateFile,Json.Serialize(result));
    var process=Process.Start(new ProcessStartInfo(Image,item[1]+" \""+report+"\""){UseShellExecute=false,CreateNoWindow=true});
    process.WaitForExit();int code=process.ExitCode;process.Dispose();var proof=File.Exists(report)?Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(report)):null;
    bool ok=code==0&&proof!=null&&proof.ContainsKey("Success")&&(bool)proof["Success"];
    if(item[1]!="--colocate")ok=ok&&proof.ContainsKey("AutomaticRestored")&&(bool)proof["AutomaticRestored"];
    jobs.Add(new{Name=item[0],ExitCode=code,Success=ok,Report=Path.GetFileName(report)});Atomic.Write(stateFile,Json.Serialize(result));
    if(!ok)throw new IOException("Batch stopped after failed stage: "+item[0]);
    if(item[0]=="banked1"){int[] high=LastLevel(Path.Combine(root,"banked7.json.worker.json"),7),low=LastLevel(Path.Combine(root,"banked1.json.worker.json"),1);result["MeasuredLevels"]=new{HighFan1=high[0],HighFan2=high[1],LowFan1=low[0],LowFan2=low[1]};if(high[0]-low[0]<500||high[1]-low[1]<500)throw new IOException("Two distinct measured speeds required on BOTH fans");}
   }
   result["Success"]=true;Atomic.Write(stateFile,Json.Serialize(result));return 0;
  }catch(Exception e){result["Success"]=false;result["Error"]=e.Message;Atomic.Write(stateFile,Json.Serialize(result));return 1;}
 }
}
