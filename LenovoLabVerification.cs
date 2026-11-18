using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using ThinkPadModesV3;
internal static class LenovoLabVerification {
 public static int Run(string report){
  var json=new JavaScriptSerializer();string file=Path.Combine(Path.GetDirectoryName(typeof(LenovoLabVerification).Assembly.Location),"lenovo-sdk-series.json.private-data.json");
  if(!File.Exists(file))file=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(typeof(LenovoLabVerification).Assembly.Location)),"lenovo-sdk-series.json.private-data.json");
  var tests=new List<object>();string before=Environment.GetEnvironmentVariable("TPM_LAB_GUARD_NAME");Environment.SetEnvironmentVariable("TPM_LAB_GUARD_NAME",null);
  try{for(int test=0;test<9;test++){var raw=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(file));Dictionary<string,object> fan=null;foreach(var m in (object[])raw["moduleList"]){var d=(Dictionary<string,object>)m;if(Convert.ToString(d["moduleName"])=="FAN")fan=d;}if(fan==null)throw new IOException("Recorded FAN fixture missing");var devices=(object[])fan["deviceList"];string name="recorded-two-fans";
    if(test==1){name="reject-missing-secondary";fan["deviceList"]=new object[]{devices[0]};}
    if(test==2){name="reject-stop-mode";((Dictionary<string,object>)devices[0])["ctrl_value"]=0;}
    if(test==3){name="reject-duplicate-primary";fan["deviceList"]=new object[]{devices[0],devices[0],devices[1]};}
    if(test==4){name="reject-unrequested-manual";((Dictionary<string,object>)devices[0])["ctrl_value"]=7;}
    if(test==5){name="accept-firmware-auto-plus-level4";foreach(var d in devices)((Dictionary<string,object>)d)["ctrl_value"]=132;}
    if(test==6){name="accept-firmware-auto-plus-level7";foreach(var d in devices)((Dictionary<string,object>)d)["ctrl_value"]=135;}
    if(test==7){name="reject-fullspeed-disengaged";((Dictionary<string,object>)devices[0])["ctrl_value"]=64;}
    if(test==8){name="reject-invalid-flags";((Dictionary<string,object>)devices[0])["ctrl_value"]=255;}
    bool rejected=false;try{LenovoSdkProbe.FanSummary(raw);}catch(IOException){rejected=true;}tests.Add(new{Name=name,Passed=(test==0||test==5||test==6)?!rejected:rejected});
   }
   bool passed=true;foreach(var row in tests){var r=json.Deserialize<Dictionary<string,object>>(json.Serialize(row));if(!(bool)r["Passed"])passed=false;}
   Atomic.Write(report,json.Serialize(new{UTC=DateTime.UtcNow.ToString("o"),HardwareWrites=false,RecordedFixture=true,Tests=tests,Success=passed}));return passed?0:1;
  }finally{Environment.SetEnvironmentVariable("TPM_LAB_GUARD_NAME",before);}
 }
}
