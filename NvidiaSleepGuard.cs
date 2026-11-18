using System;
using System.Runtime.InteropServices;

internal sealed class NvidiaSleepGuard
{
    private const string DisplayClass = "{4D36E968-E325-11CE-BFC1-08002BE10318}";
    private const uint PresentDisplayDevices = 0x300;
    private const uint BinaryProperty = 0x1003;
    private const uint DeviceD3 = 4;
    internal const int RequiredSleepSeconds = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string instance, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling=true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, ref PropertyKey key, out uint type, [Out] uint[] data, ref uint length, uint flags);

    private readonly uint node;
    private readonly uint[] data = new uint[32];
    private PropertyKey power = new PropertyKey { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 32 };
    private long since = -1, last = -1;

    private NvidiaSleepGuard(uint node) { this.node = node; }

    internal static NvidiaSleepGuard Open()
    {
        uint size;
        if (CM_Get_Device_ID_List_SizeW(out size, DisplayClass, PresentDisplayDevices) != 0 || size < 2 || size > 65536)
            return null;
        var ids = new char[size];
        if (CM_Get_Device_ID_ListW(DisplayClass, ids, size, PresentDisplayDevices) != 0)
            return null;
        string match = null;
        foreach (string id in new string(ids).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!id.StartsWith(@"PCI\VEN_10DE&", StringComparison.OrdinalIgnoreCase))
                continue;
            if (match != null) return null;
            match = id;
        }
        uint devNode;
        return match != null && CM_Locate_DevNodeW(out devNode, match, 0) == 0 ? new NvidiaSleepGuard(devNode) : null;
    }

    internal bool Observe(long now)
    {
        uint size = (uint)(data.Length * sizeof(uint)), type;
        bool sleep = CM_Get_DevNode_PropertyW(node, ref power, out type, data, ref size, 0) == 0 &&
            type == BinaryProperty && size >= 8 && data[0] >= 8 && data[0] <= size && data[1] == DeviceD3;
        Update(ref since, ref last, now, sleep);
        return sleep;
    }

    internal void Reset() { since = last = -1; }

    internal int SecondsRemaining(long now) { return SecondsRemaining(since, now); }

    internal static int SecondsRemaining(long since, long now)
    {
        if (since < 0 || now < since) return RequiredSleepSeconds;
        long remaining = RequiredSleepSeconds * 1000L - (now - since);
        return remaining <= 0 ? 0 : (int)((remaining + 999) / 1000);
    }

    internal static void Update(ref long since, ref long last, long now, bool sleep)
    {
        if (!sleep || now < last || now - last > 3500) since = -1;
        if (sleep && since < 0) since = now;
        last = now;
    }
}
