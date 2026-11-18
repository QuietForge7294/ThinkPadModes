using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
internal sealed class LenovoEcTransport:IDisposable {
 internal const string DriverHash="DC5BAE2E403E639EF2EBAB4C85B827F75853ED6CFBD122A847784F3D49A79E51";
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool DeviceIoControl(SafeFileHandle h,uint code,byte[] input,int length,byte[] output,int capacity,out int returned,IntPtr overlapped);
 readonly SafeFileHandle handle;readonly Mutex mutex;readonly Stopwatch watch=Stopwatch.StartNew();readonly bool permitModes;
 public readonly List<object> Trace=new List<object>();public int Reads,SelectorWrites,ModeWrites;byte status;string phase="opening";int previous=-1;
 public static void Verify(){using(var key=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS")){if(Convert.ToString(key.GetValue("SystemProductName"))!="21QTS2AH00"||Convert.ToString(key.GetValue("BIOSVersion"))!="R2WET45W (1.27 )"||Convert.ToString(key.GetValue("ECFirmwareMajorRelease"))+"."+Convert.ToString(key.GetValue("ECFirmwareMinorRelease"))!="1.17")throw new InvalidOperationException("Model/BIOS/EC differs");}string file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"Lenovo\Lenovo Diagnostics Evolution\LenovoDiagnosticsDriver.sys");using(var sha=SHA256.Create())using(var stream=File.OpenRead(file))if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","")!=DriverHash)throw new InvalidDataException("Qualified driver hash differs");}
 public LenovoEcTransport(bool allowModes=false){Verify();permitModes=allowModes;mutex=new Mutex(false,@"Global\Access_EC");bool locked=false;try{try{locked=mutex.WaitOne(300);}catch(AbandonedMutexException){mutex.ReleaseMutex();throw new IOException("Abandoned EC mutex");}if(!locked)throw new IOException("EC mutex busy");handle=CreateFile(@"\\.\LenovoDiagnosticsDriver",0xC0000000,0,IntPtr.Zero,3,0,IntPtr.Zero);if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error(),"Lenovo open");}catch{if(locked)mutex.ReleaseMutex();mutex.Dispose();if(handle!=null)handle.Dispose();throw;}}
 void Record(object item){if(Trace.Count<300)Trace.Add(item);}
 byte PortRead(ushort port){if(port!=0x62&&port!=0x66)throw new InvalidOperationException();byte[] input=new byte[8];input[0]=(byte)port;input[4]=1;byte[] output=new byte[1];int count;if(!DeviceIoControl(handle,0x222018,input,8,output,1,out count,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"Lenovo read");if(count!=1)throw new IOException("Read byte count");if(port==0x62||previous!=output[0])Record(new{Ms=watch.ElapsedMilliseconds,Phase=phase,Port=port,Read=output[0]});if(port==0x66){status=output[0];previous=status;}return output[0];}
 void PortWrite(ushort port,byte value){if(port!=0x62&&port!=0x66)throw new InvalidOperationException();byte[] input=new byte[12];input[0]=(byte)port;input[4]=1;input[8]=value;int count;if(!DeviceIoControl(handle,0x22201C,input,12,null,0,out count,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"Lenovo write");if(count!=1)throw new IOException("Write byte count");Record(new{Ms=watch.ElapsedMilliseconds,Phase=phase,Port=port,Write=value});}
 void Wait(byte mask,byte expected){long start=watch.ElapsedMilliseconds;int polls=0;do{if((PortRead(0x66)&mask)==expected)return;if(++polls>32)Thread.Sleep(1);}while(watch.ElapsedMilliseconds-start<100);throw new TimeoutException(phase+" status0x"+status.ToString("X2")+" timeout100ms");}
 void Ready(){Wait(2,0);if((PortRead(0x66)&1)!=0)throw new IOException("Pending output; no buffer drain");}
 public byte Read(byte address){if(address!=0x31&&address!=0x2F&&address!=0x84&&address!=0x85&&address!=0x78&&address!=0x79)throw new ArgumentException("Read address not allowed");phase="RD-ready-"+address.ToString("X2");Ready();phase="RD-command";PortWrite(0x66,0x80);Wait(2,0);phase="RD-address-"+address.ToString("X2");PortWrite(0x62,address);phase="RD-data-"+address.ToString("X2");Wait(3,1);Reads++;return PortRead(0x62);}
 void WriteRam(byte address,byte value){if(address!=0x31&&address!=0x2F)throw new ArgumentException("Write address not allowed");if(address==0x2F&&(!permitModes||(value!=0x80&&(value<1||value>7))))throw new InvalidOperationException("Mode write locked");phase="WR-ready-"+address.ToString("X2");Ready();phase="WR-command";PortWrite(0x66,0x81);Wait(2,0);phase="WR-address-"+address.ToString("X2");PortWrite(0x62,address);Wait(2,0);phase="WR-data-"+address.ToString("X2");PortWrite(0x62,value);Wait(2,0);if(address==0x31)SelectorWrites++;else ModeWrites++;}
 public void Select(bool second){byte before=Read(0x31);byte value=(byte)((before&0xFE)|(second?1:0));if(value!=before)WriteRam(0x31,value);byte actual=Read(0x31);if(actual!=value)throw new IOException("Selector readback differs");}
 public int? Rpm(){byte low=Read(0x84),high=Read(0x85);int raw=low|(high<<8);if(raw==65535)return null;if(raw>20000)throw new IOException("Unqualified tachometer value");return raw;}
 public void SetMode(byte value){if(!permitModes)throw new InvalidOperationException("Mode write locked");WriteRam(0x2F,value);byte actual=Read(0x2F);if(value==0x80?(actual&0x80)==0:actual!=value)throw new IOException("Mode readback differs");}
 public void Dispose(){handle.Dispose();mutex.ReleaseMutex();mutex.Dispose();}
}
