using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
 public sealed class EmiMeter : IDisposable {
  static readonly Guid InterfaceId=new Guid("45BD8344-7ED6-49cf-A440-C276C933B053");
  readonly Microsoft.Win32.SafeHandles.SafeFileHandle handle;
  readonly int channelCount;
  readonly int package;
  public string Channel {get; private set;}
  [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)]
  static extern uint CM_Get_Device_Interface_List_SizeW(out uint size,ref Guid guid,string instance,uint flags);
  [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)]
  static extern uint CM_Get_Device_Interface_ListW(ref Guid guid,string instance,[Out] char[] list,uint size,uint flags);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr attributes,uint disposition,uint flags,IntPtr template);
  [DllImport("kernel32.dll",SetLastError=true)]
  [return:MarshalAs(UnmanagedType.Bool)]
  static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h,uint code,IntPtr input,uint inputSize,byte[] output,uint outputSize,out uint returned,IntPtr overlapped);
  static byte[] Read(Microsoft.Win32.SafeHandles.SafeFileHandle h,uint request,int size) {
   var buffer=new byte[size];
   uint returned;
   if(!DeviceIoControl(h,0x224000+4*request,IntPtr.Zero,0,buffer,(uint)size,out returned,IntPtr.Zero))
    throw new Win32Exception(Marshal.GetLastWin32Error());
   if(returned!=size) throw new InvalidOperationException("Réponse EMI tronquée.");
   return buffer;
  }
  EmiMeter(Microsoft.Win32.SafeHandles.SafeFileHandle h) {
   if(BitConverter.ToUInt16(Read(h,0,2),0)!=2) throw new NotSupportedException("Version EMI différente de la version 2.");
   uint size=BitConverter.ToUInt32(Read(h,1,4),0);
   if(size<68 || size>65536) throw new InvalidOperationException("Taille des métadonnées EMI invalide.");
   byte[] metadata=Read(h,2,(int)size);
   channelCount=BitConverter.ToUInt16(metadata,66);
   package=-1;
   int offset=68;
   for(int i=0;i<channelCount;i++) {
    if(offset+6>metadata.Length) throw new InvalidOperationException("Canal EMI tronqué.");
    uint unit=BitConverter.ToUInt32(metadata,offset);
    int length=BitConverter.ToUInt16(metadata,offset+4);
    if(length<2 || length%2!=0 || offset+6+length>metadata.Length)
     throw new InvalidOperationException("Nom de canal EMI invalide.");
    string name=System.Text.Encoding.Unicode.GetString(metadata,offset+6,length).TrimEnd('\0');
    if(unit==0 && name=="RAPL_Package0_PKG") {
     if(package>=0) throw new InvalidOperationException("Plusieurs canaux de package identiques.");
     package=i;
     Channel=name;
    }
    offset+=6+length;
   }
   if(package<0) throw new NotSupportedException("Canal RAPL du package Intel absent.");
   handle=h;
  }
  public static EmiMeter Open() {
   Guid guid=InterfaceId;
   uint size;
   uint result=CM_Get_Device_Interface_List_SizeW(out size,ref guid,null,0);
   if(result!=0 || size<2 || size>262144) throw new InvalidOperationException("Interface EMI non disponible ("+result+").");
   var list=new char[size];
   result=CM_Get_Device_Interface_ListW(ref guid,null,list,size,0);
   if(result!=0) throw new InvalidOperationException("Énumération EMI impossible ("+result+").");
   string failure="Canal RAPL du package Intel absent.";
   foreach(string path in new string(list).Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries)) {
    var h=CreateFileW(path,0x80000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
    try {
     if(h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
     return new EmiMeter(h);
    } catch(Exception ex) { failure=ex.Message; h.Dispose(); }
   }
   throw new InvalidOperationException(failure);
  }
  public ulong[] Sample() {
   byte[] data=Read(handle,3,checked(channelCount*16));
   return new[]{BitConverter.ToUInt64(data,package*16),BitConverter.ToUInt64(data,package*16+8)};
  }
  public void Dispose() {handle.Dispose();}
 }
