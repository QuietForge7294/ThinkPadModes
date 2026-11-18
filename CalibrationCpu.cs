using System;using System.IO;using System.Runtime.InteropServices;using ThinkPadModesV3;
internal sealed class CalibrationCpu:IDisposable {
 readonly PawnModule module;readonly double tj;
 [DllImport("kernel32.dll")]static extern IntPtr GetCurrentThread();
 [DllImport("kernel32.dll",SetLastError=true)]static extern UIntPtr SetThreadAffinityMask(IntPtr t,UIntPtr m);
 internal CalibrationCpu(){var i=Paths.Info();module=new PawnModule("IntelMSR.bin",Path.Combine(Paths.Root,"versions",i.Version,"modules"));tj=(Read(0x1A2)>>16)&255;if(tj!=105){module.Dispose();throw new IOException("CPU TjMax differs from qualified105C");}}
 ulong Read(uint reg){if(reg!=0x1A2&&reg!=0x1B1)throw new ArgumentException();var previous=SetThreadAffinityMask(GetCurrentThread(),new UIntPtr(1));if(previous==UIntPtr.Zero)throw new IOException("CPU affinity unavailable");try{return module.Execute("ioctl_read_msr",reg);}finally{SetThreadAffinityMask(GetCurrentThread(),previous);}}
 internal double Temperature(){ulong s=Read(0x1B1);if((s&0x80000000)==0)throw new IOException("CPU DTS invalid");double c=tj-((s>>16)&127);if(c<0||c>tj)throw new IOException("CPU DTS out of range");return c;}
 public void Dispose(){module.Dispose();}
}
