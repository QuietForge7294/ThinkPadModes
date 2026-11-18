using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
namespace ThinkPadModesV3 {
public sealed class Settings {
    public int SchemaVersion=1;
    public string Model="21QTS2AH00";
    public string Bios="R2WET45W (1.27 )";
    public string EcVersion="1.17";
    public string PerformanceAc="Normal";
    public string PerformanceDc="Normal";
    public string Cooling="Auto";
    public bool ZeroDb;
    public void Validate(){if(SchemaVersion!=1)throw new InvalidDataException("Unsupported settings version");if(Model!="21QTS2AH00"||String.IsNullOrWhiteSpace(Bios)||EcVersion!="1.17")throw new InvalidDataException("Settings hardware identity mismatch");foreach(var m in new[]{PerformanceAc,PerformanceDc})if(m!="Eco"&&m!="Normal"&&m!="Turbo")throw new InvalidDataException("Unknown performance mode");if(Cooling!="Auto"&&Cooling!="Bas"&&Cooling!="Renforce")throw new InvalidDataException("Unknown cooling request");}
}
public sealed class SettingsStore {
    readonly string path;readonly object gate=new object();public string LoadStatus {get;private set;}
    public SettingsStore(string path){this.path=path;}
    static readonly string[] Keys={"SchemaVersion","Model","Bios","EcVersion","PerformanceAc","PerformanceDc","Cooling","ZeroDb"};
    static Settings Read(string file){string json=File.ReadAllText(file);if(json.Length>65536)throw new InvalidDataException("Settings size limit");var serializer=new JavaScriptSerializer();var obj=serializer.DeserializeObject(json) as Dictionary<string,object>;if(obj==null||obj.Keys.Any(k=>!Keys.Contains(k))||Keys.Any(k=>!obj.ContainsKey(k)))throw new InvalidDataException("Settings schema mismatch");Settings s=serializer.Deserialize<Settings>(json);s.Validate();return s;}
    public Settings Load(){lock(gate){if(!File.Exists(path)&&!File.Exists(path+".lastgood")){LoadStatus="none";return null;}try{Settings s=Read(path);LoadStatus="primary";return s;}catch(Exception e){try{Settings s=Read(path+".lastgood");LoadStatus="backup: "+e.Message;return s;}catch{LoadStatus="invalid: "+e.Message;return null;}}}}
    public void Save(Settings settings){lock(gate){settings.Validate();Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));if(File.Exists(path)){try{Settings previous=Read(path);Atomic.Write(path+".lastgood",new JavaScriptSerializer().Serialize(previous));}catch(InvalidDataException){}catch(ArgumentException){}}
        string json=new JavaScriptSerializer().Serialize(settings);Atomic.Write(path,json);Settings actual=Read(path);if(new JavaScriptSerializer().Serialize(actual)!=json)throw new IOException("Settings readback mismatch");if(!File.Exists(path+".lastgood"))Atomic.Write(path+".lastgood",json);LoadStatus="saved-and-readback";
    }}
}
public static class Atomic {
    public static void Write(string path,string json){Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";byte[] b=new System.Text.UTF8Encoding(false).GetBytes(json);try{using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(b,0,b.Length);f.Flush(true);}if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}}
}
public sealed class Qualification {
    public int SchemaVersion=1;public string Model;public string Bios;public string EcVersion;public string Backend;
    public bool RpmValidated;public bool ControlValidated;public bool GuardianValidated;public bool ZeroDbValidated;
    public int LowLevel;public int HighLevel;public double CpuAbort;public double GpuAbort;public double CpuZeroEntry;public double GpuZeroEntry;public double ZeroHysteresis;public int CpuCapPercent;public int MaxAgeMs;public int GuardianDeadlineMs;public string EvidenceSha256;
    public bool Matches(string model,string bios,string ec){return SchemaVersion==1&&Model==model&&Bios==bios&&EcVersion==ec&&Backend=="PawnIO-2.2.0/LpcACPIEC-0.2.11";}
    public bool ManualAllowed {get{return RpmValidated&&ControlValidated&&GuardianValidated&&LowLevel>=1&&LowLevel<=7&&HighLevel>=LowLevel&&HighLevel<=7&&CpuAbort>0&&GpuAbort>0&&MaxAgeMs>0&&MaxAgeMs<=6000&&GuardianDeadlineMs>0&&GuardianDeadlineMs<=3000&&EvidenceSha256!=null&&EvidenceSha256.Length==64;}}
    public bool ZeroAllowed {get{return ManualAllowed&&ZeroDbValidated&&CpuZeroEntry>0&&GpuZeroEntry>0&&CpuZeroEntry+ZeroHysteresis<CpuAbort&&GpuZeroEntry+ZeroHysteresis<GpuAbort&&ZeroHysteresis>0&&CpuCapPercent>=1&&CpuCapPercent<=99;}}
}
public sealed class ThermalFrame {
    public double? Cpu;public double? Gpu;public int? Fan1;public int? Fan2;public int AgeMs;public bool GuardianAlive;public bool IdentityMatches;public bool CpuCapVerified;
    public bool Valid {get{return Cpu.HasValue&&Gpu.HasValue&&Cpu.Value>=0&&Gpu.Value>=0&&Fan1.HasValue&&Fan2.HasValue&&Fan1>=0&&Fan2>=0;}}
}
public interface ICoolingActuator {void RestoreAutomatic();void SetNonzeroLevel(int level);void StopFans();}
public sealed class ThermalPolicy {
    readonly ICoolingActuator actuator;readonly Qualification qualification;string applied="Auto";long coolSince=-1;
    public string State {get;private set;}public string Detail {get;private set;}
    public ThermalPolicy(ICoolingActuator actuator,Qualification q){this.actuator=actuator;qualification=q;State="Auto Lenovo";}
    void Auto(string state,string detail){coolSince=-1;if(applied!="Auto"){actuator.RestoreAutomatic();applied="Auto";}State=state;Detail=detail;}
    void Level(int level){string next=level.ToString();if(applied!=next){actuator.SetNonzeroLevel(level);applied=next;}State="Manuel surveille";Detail="Niveau "+level;}
    public void Step(string request,bool zero,ThermalFrame frame,long now){
      if(request!="Auto"&&request!="Bas"&&request!="Renforce")throw new ArgumentException("Unknown cooling request");
      try{
       if(request=="Auto"&&!zero){Auto("Auto Lenovo","Controle firmware");return;}
       if(!qualification.ManualAllowed||!frame.IdentityMatches){Auto("Qualification incomplete","Commandes manuelles verrouillees");return;}
       if(!frame.GuardianAlive||!frame.Valid||frame.AgeMs<0||frame.AgeMs>qualification.MaxAgeMs){Auto("Defaut","Capteurs ou gardien indisponibles");return;}
       if(frame.Cpu>=qualification.CpuAbort||frame.Gpu>=qualification.GpuAbort){Auto("Refroidissement prioritaire","Limite thermique de qualification");return;}
       if(zero){
        if(!qualification.ZeroAllowed||!frame.CpuCapVerified){Auto("Attente 0 dB","Limitation CPU non qualifiee ou non confirmee");return;}
        double hysteresis=applied=="0"?qualification.ZeroHysteresis:0;
        if(frame.Cpu>qualification.CpuZeroEntry+hysteresis||frame.Gpu>qualification.GpuZeroEntry+hysteresis){Auto("Attente 0 dB","Refroidissement necessaire");return;}
        if(coolSince<0)coolSince=now;
        if(applied!="0"&&now-coolSince<60000){AutoPreserveWait("Conditions observees pendant 60 s");return;}
        if(applied!="0"){actuator.StopFans();applied="0";}
        State=frame.Fan1==0&&frame.Fan2==0?"0 dB mesure":"Attente 0 dB";Detail=State=="0 dB mesure"?"Deux tachymetres a zero":"Arret demande, mesure attendue";return;
       }
       coolSince=-1;Level(request=="Bas"?qualification.LowLevel:qualification.HighLevel);
      }catch(Exception e){try{actuator.RestoreAutomatic();applied="Auto";}catch(Exception recovery){State="Defaut critique";Detail=e.Message+"; restauration non confirmee: "+recovery.Message;throw new IOException(Detail,e);}State="Defaut";Detail=e.Message;}
    }
    void AutoPreserveWait(string detail){if(applied!="Auto"){actuator.RestoreAutomatic();applied="Auto";}State="Attente 0 dB";Detail=detail;}
    public void Shutdown(){actuator.RestoreAutomatic();applied="Auto";State="Auto Lenovo";}
}
}
