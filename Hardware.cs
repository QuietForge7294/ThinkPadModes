using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using TimeoutException = System.TimeoutException;
namespace ThinkPadModesV3 {
public sealed class SensorValue {
    public double? Value; public string Unit; public string Source; public string Status;
    public string AcquiredUtc; public long MonotonicMs;
    public static SensorValue Valid(double value,string unit,string source) {return new SensorValue {Value=value,Unit=unit,Source=source,Status="valid",AcquiredUtc=DateTime.UtcNow.ToString("o"),MonotonicMs=Clock.Ms};}
    public static SensorValue Missing(string unit,string source,string error) {return new SensorValue {Unit=unit,Source=source,Status=error,AcquiredUtc=DateTime.UtcNow.ToString("o"),MonotonicMs=Clock.Ms};}
}
public static class Clock {private static readonly Stopwatch watch=Stopwatch.StartNew(); public static long Ms {get{return watch.ElapsedMilliseconds;}}}
public static class Digest {
    public static string Of(string path) {using(var h=SHA256.Create())using(var s=File.OpenRead(path))return BitConverter.ToString(h.ComputeHash(s)).Replace("-","");}
}
internal sealed class PawnModule : IDisposable {
    const uint Type=41394; readonly SafeFileHandle handle;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string file,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle h,uint code,byte[] input,int inSize,byte[] output,int outSize,out int returned,IntPtr overlapped);
    public PawnModule(string module,string moduleRoot=null) {
        if(module!="IntelMSR.bin" && module!="LpcACPIEC.bin")throw new ArgumentException("Module not allowed");
        string path=Path.Combine(moduleRoot??Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"modules"),module);
        string expected=module=="IntelMSR.bin"?"D6ED85D65AB17A22F813EF98207D6D537155EE2DED5976A21CB48413C9B92E5F":"C38FD116E7AFF4D1FDB0A494E296BE0A6708E5A22FC72F14587442FB7F8F7906";
        if(Digest.Of(path)!=expected)throw new InvalidDataException("Official module digest mismatch: "+module);
        using(var service=new ServiceController("PawnIO")) {if(service.Status!=ServiceControllerStatus.Running){service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(8));}}
        handle=CreateFile(@"\\?\GLOBALROOT\Device\PawnIO",0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error(),"PawnIO open failed");
        try {byte[] binary=File.ReadAllBytes(path);Call(0x821,binary,0);} catch {handle.Dispose();throw;}
    }
    byte[] Call(uint function,byte[] input,int length) {byte[] output=length==0?null:new byte[length];int returned; if(!DeviceIoControl(handle,(Type<<16)|(function<<2),input,input==null?0:input.Length,output,length,out returned,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"PawnIO ioctl "+function.ToString("X"));if(returned!=length)throw new IOException("PawnIO output size mismatch");return output;}
    public ulong Execute(string function,params ulong[] arguments) {
        byte[] input=new byte[32+arguments.Length*8];byte[] name=System.Text.Encoding.ASCII.GetBytes(function);if(name.Length>=32)throw new ArgumentException();Array.Copy(name,input,name.Length);
        for(int i=0;i<arguments.Length;i++)Array.Copy(BitConverter.GetBytes(arguments[i]),0,input,32+8*i,8);
        int output=function=="ioctl_pio_write"||function=="ioctl_write_msr"?0:8;byte[] b=Call(0x841,input,output);return output==0?0:BitConverter.ToUInt64(b,0);
    }
    public void Dispose(){handle.Dispose();}
}
internal interface IEcBus {byte Read(byte address); void SelectFan(bool second); IDisposable Lock(int milliseconds);}
// No fan-mode write path is present in this first qualification adapter.
internal sealed class EcBus : IEcBus,IDisposable {
    readonly PawnModule module;readonly Mutex mutex;readonly int timeout;byte address,lastStatus;string phase="opening";int lastLogged=-1;
    public readonly List<object> Trace=new List<object>();
    public EcBus(int timeoutMs=100){if(timeoutMs<100||timeoutMs>1000)throw new ArgumentOutOfRangeException();timeout=timeoutMs;module=new PawnModule("LpcACPIEC.bin");try{mutex=new Mutex(false,@"Global\Access_EC");}catch{module.Dispose();throw;}}
    sealed class Lease:IDisposable {Mutex owner;public Lease(Mutex owner){this.owner=owner;}public void Dispose(){if(owner!=null){owner.ReleaseMutex();owner=null;}}}
    public IDisposable Lock(int milliseconds){try{if(!mutex.WaitOne(milliseconds))throw new TimeoutException("EC mutex busy");}catch(AbandonedMutexException){mutex.ReleaseMutex();throw new IOException("Abandoned EC transaction; qualification suspended");}return new Lease(mutex);}
    byte Port(byte port){byte result=checked((byte)module.Execute("ioctl_pio_read",port));if(port==0x66)lastStatus=result;if(Trace.Count<100&&(port!=0x66||lastLogged!=result)){Trace.Add(new {Ms=Clock.Ms,Operation="read-port",Port=port,Value=result,Phase=phase});lastLogged=result;}return result;}
    void Port(byte port,byte value){if(Trace.Count<100)Trace.Add(new {Ms=Clock.Ms,Operation="write-port",Port=port,Value=value,Phase=phase});module.Execute("ioctl_pio_write",port,value);}
    void Wait(byte mask,bool set){long start=Clock.Ms;do{if(((Port(0x66)&mask)!=0)==set)return;Thread.Sleep(1);}while(Clock.Ms-start<timeout);throw new TimeoutException("EC "+phase+" register 0x"+address.ToString("X2")+" status 0x"+lastStatus.ToString("X2")+" timeout "+timeout+"ms; no automatic retry");}
    void Ready(){Wait(2,false);if((Port(0x66)&1)!=0)throw new IOException("EC output pending; no blind buffer drain");}
    public byte Read(byte address){if(address!=0x2F && address!=0x31 && address!=0x78 && address!=0x79 && address!=0x83 && address!=0x84 && address!=0x85)throw new ArgumentException("EC read not qualified");this.address=address;phase="ready";Ready();phase="read-command";Port(0x66,0x80);Wait(2,false);phase="read-address";Port(0x62,address);Wait(2,false);phase="read-data";Wait(1,true);return Port(0x62);}
    public void SelectFan(bool second){byte original=Read(0x31);byte next=(byte)((original&0xFE)|(second?1:0));if(next==original)return;Ready();Port(0x66,0x81);Wait(2,false);Port(0x62,0x31);Wait(2,false);Port(0x62,next);Wait(2,false);}
    public void Dispose(){mutex.Dispose();module.Dispose();}
}
internal sealed class FanSample {
    public int? Rpm1;public int? Rpm2;public int? Raw1;public int? Raw2;public int? SelectedRaw;public int SelectorBefore;public int ModeBefore;public int ModeAfter;
    public string AcquisitionUtc;public string Status;public int Ec78Raw;public int Ec79Raw;
}
internal static class FanAcquisition {
    static int Tach(IEcBus bus){byte low=bus.Read(0x84);byte high=bus.Read(0x85);return low|(high<<8);}
    static int? Decode(int raw){return raw==65535?(int?)null:raw;}
    public static FanSample Read(IEcBus bus,bool dual){using(bus.Lock(300)){
        var s=new FanSample {AcquisitionUtc=DateTime.UtcNow.ToString("o"),SelectorBefore=bus.Read(0x31),ModeBefore=bus.Read(0x2F)};
        if(dual){try{bus.SelectFan(false);s.Raw1=Tach(bus);bus.SelectFan(true);s.Raw2=Tach(bus);}finally{bus.SelectFan(false);}}
        else{s.SelectedRaw=Tach(bus);if((s.SelectorBefore&1)==0)s.Raw1=s.SelectedRaw;else s.Raw2=s.SelectedRaw;}
        s.Ec78Raw=bus.Read(0x78);s.Ec79Raw=bus.Read(0x79);
        s.ModeAfter=bus.Read(0x2F);s.Rpm1=s.Raw1.HasValue?Decode(s.Raw1.Value):null;s.Rpm2=s.Raw2.HasValue?Decode(s.Raw2.Value):null;
        s.Status=s.ModeBefore!=s.ModeAfter?"mode_changed_externally":s.Raw1==65535||s.Raw2==65535?"unqualified_tach_sentinel":"unqualified_hardware_sample";return s;
    }}
}
internal sealed class CpuTelemetry:IDisposable {
    readonly object computer;readonly Type type;readonly PawnModule msr;string lhmError;
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll",SetLastError=true)] static extern UIntPtr SetThreadAffinityMask(IntPtr thread,UIntPtr mask);
    public CpuTelemetry(){
        string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"lhm");
        AppDomain.CurrentDomain.AssemblyResolve+=(sender,args)=>{string file=Path.Combine(dir,new AssemblyName(args.Name).Name+".dll");return File.Exists(file)?Assembly.LoadFrom(file):null;};
        try{var assembly=Assembly.LoadFrom(Path.Combine(dir,"LibreHardwareMonitorLib.dll"));type=assembly.GetType("LibreHardwareMonitor.Hardware.Computer",true);computer=Activator.CreateInstance(type);type.GetProperty("IsCpuEnabled").SetValue(computer,true,null);type.GetMethod("Open").Invoke(computer,null);}catch(Exception e){lhmError=e.GetBaseException().Message;}
        msr=new PawnModule("IntelMSR.bin");
    }
    ulong Msr(uint register){if(register!=0x1A2&&register!=0x1B1&&register!=0x606&&register!=0x611)throw new ArgumentException();UIntPtr old=SetThreadAffinityMask(GetCurrentThread(),new UIntPtr(1));if(old==UIntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{return msr.Execute("ioctl_read_msr",register);}finally{SetThreadAffinityMask(GetCurrentThread(),old);}}
    public Dictionary<string,SensorValue> Read(){var r=new Dictionary<string,SensorValue>();
        if(computer!=null&&lhmError==null)try{foreach(object hw in (IEnumerable)type.GetProperty("Hardware").GetValue(computer,null)){Type ht=hw.GetType();ht.GetMethod("Update").Invoke(hw,null);foreach(object sensor in (IEnumerable)ht.GetProperty("Sensors").GetValue(hw,null)){Type st=sensor.GetType();string name=(string)st.GetProperty("Name").GetValue(sensor,null);string kind=st.GetProperty("SensorType").GetValue(sensor,null).ToString();object value=st.GetProperty("Value").GetValue(sensor,null);if(name=="CPU Package"&&value!=null&&(kind=="Temperature"||kind=="Power"))r[kind=="Temperature"?"CpuPackage":"CpuWatts"]=SensorValue.Valid(Convert.ToDouble(value),kind=="Temperature"?"C":"W","LibreHardwareMonitor 0.9.6/PawnIO");}}}catch(Exception e){lhmError=e.GetBaseException().Message;}
        try{ulong target=Msr(0x1A2),status=Msr(0x1B1);double tj=(target>>16)&255;r["CpuTjMax"]=SensorValue.Valid(tj,"C","IntelMSR 0x1A2");if(tj<60||tj>125)throw new IOException("Invalid temperature target");if((status&0x80000000)==0)throw new IOException("Package DTS invalid bit");double actual=tj-((status>>16)&127);r["CpuPackageMsr"]=SensorValue.Valid(actual,"C","IntelMSR 0x1B1/0x1A2, CPU0");if(!r.ContainsKey("CpuPackage"))r["CpuPackage"]=r["CpuPackageMsr"];}catch(Exception e){r["CpuPackageMsr"]=SensorValue.Missing("C","IntelMSR",e.Message);}
        if(!r.ContainsKey("CpuPackage"))r["CpuPackage"]=SensorValue.Missing("C","LHM/IntelMSR",lhmError??"No package sensor");return r;
    }
    public void Dispose(){if(computer!=null)try{type.GetMethod("Close").Invoke(computer,null);}catch{}msr.Dispose();}
}
internal sealed class NvmlTelemetry:IDisposable {
    readonly IntPtr library;IntPtr device;bool opened;
    [StructLayout(LayoutKind.Sequential)]struct Util{public uint Gpu;public uint Memory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryEx(string file,IntPtr reserved,uint flags);
    [DllImport("kernel32.dll")]static extern bool FreeLibrary(IntPtr module);
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)]static extern int nvmlInit_v2();
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)]static extern int nvmlShutdown();
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)]static extern int nvmlDeviceGetHandleByIndex_v2(uint index,out IntPtr device);
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)]static extern int nvmlDeviceGetTemperature(IntPtr device,uint kind,out uint value);
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)]static extern int nvmlDeviceGetPowerUsage(IntPtr device,out uint value);
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)]static extern int nvmlDeviceGetUtilizationRates(IntPtr device,out Util value);
    public NvmlTelemetry(){library=LoadLibraryEx(Path.Combine(Environment.SystemDirectory,"nvml.dll"),IntPtr.Zero,0x800);if(library==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());if(nvmlInit_v2()!=0)throw new IOException("NVML initialization failed");opened=true;if(nvmlDeviceGetHandleByIndex_v2(0,out device)!=0)throw new IOException("NVML device unavailable");}
    // NVIDIA CUDA NVML 13.5.37 header: NVML_FI_DEV_GPU_TEMP = 308, value type DOUBLE.
    [StructLayout(LayoutKind.Sequential,Pack=8)]internal struct TemperatureField {public uint FieldId,ScopeId;public long Timestamp,Latency;public int ValueType,Return;public double Value;}
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)]static extern int nvmlDeviceGetFieldValues(IntPtr device,int count,ref TemperatureField value);
    [DllImport("kernel32.dll")]static extern void GetSystemTimePreciseAsFileTime(out long ticks);
    internal string TemperatureError;internal long TemperatureTimestamp;internal TemperatureField LastTemperatureField;internal int LastTemperatureReturn;internal long LastTemperatureNow;
    internal static bool TemperatureValid(int code,TemperatureField f,long now){return code==0&&f.FieldId==308&&f.Return==0&&f.ValueType==0&&!Double.IsNaN(f.Value)&&!Double.IsInfinity(f.Value)&&f.Value>=0&&f.Value<=150&&f.Timestamp>0&&now>=f.Timestamp&&now-f.Timestamp<=3000000;}
    internal double? TemperatureOnly(){TemperatureError=null;TemperatureTimestamp=0;try{var f=new TemperatureField{FieldId=308,Return=-1,Value=Double.NaN};int rc=nvmlDeviceGetFieldValues(device,1,ref f);long preciseTicks;GetSystemTimePreciseAsFileTime(out preciseTicks);long now=(preciseTicks-116444736000000000L)/10;LastTemperatureField=f;LastTemperatureReturn=rc;LastTemperatureNow=now;if(!TemperatureValid(rc,f,now)){TemperatureError="Température GPU indisponible ou périmée (NVML GPU_TEMP)";return null;}TemperatureTimestamp=f.Timestamp;return f.Value;}catch(EntryPointNotFoundException){TemperatureError="Champ de température NVIDIA non disponible";return null;}}
    public Dictionary<string,SensorValue> Read(){var r=new Dictionary<string,SensorValue>();double? temperature=TemperatureOnly();r["GpuTemperature"]=temperature.HasValue?SensorValue.Valid(temperature.Value,"C","NVML GPU_TEMP field308 horodaté"):SensorValue.Missing("C","NVML GPU_TEMP",TemperatureError);uint v;int code=nvmlDeviceGetPowerUsage(device,out v);r["GpuWatts"]=code==0?SensorValue.Valid(v/1000.0,"W","NVML"):SensorValue.Missing("W","NVML","error "+code);Util u;code=nvmlDeviceGetUtilizationRates(device,out u);r["GpuLoad"]=code==0?SensorValue.Valid(u.Gpu,"%","NVML"):SensorValue.Missing("%","NVML","error "+code);return r;}
    public void Dispose(){if(opened){nvmlShutdown();opened=false;}if(library!=IntPtr.Zero)FreeLibrary(library);}
}
}
