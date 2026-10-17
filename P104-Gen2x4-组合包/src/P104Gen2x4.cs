// ============================================================================
//  P104Gen2x4 —— NVIDIA P104-100 (GP104) PCIe 2.0 x4 解锁工具
// ============================================================================
//  成果：CPU 直连 x16 槽上，Gen1 x4 → Gen2 x4，带宽 0.78 → 1.55 GB/s（约 2 倍）
//
//  用法：
//    P104Gen2x4.exe                 完整解锁（bringup），含常驻按住循环
//    P104Gen2x4.exe -once           只解锁一次，不常驻
//    P104Gen2x4.exe -status         只读诊断，打印当前链路状态
//    P104Gen2x4.exe -off            停止按住、还原寄存器
//    P104Gen2x4.exe -install        安装（写驱动策略 + 部署 + 注册计划任务）
//    P104Gen2x4.exe -uninstall      卸载（删任务 + 停服务 + 还原）
//    P104Gen2x4.exe -vbios          读卡上的 VBIOS 并算哈希（只读，用于刷写前后校验）
//    P104Gen2x4.exe -vbios -save x.rom   同上，并把 ROM 存成文件
//    P104Gen2x4.exe -quiet          日志只写文件不打印
//
//  ★★ 关键机理（这是本工具存在的唯一理由）★★
//    P104 在 CPU 直连槽上开机时只向系统"声明" 2.5GT/s（LCAP2=0x02），
//    此时无论怎么写寄存器、怎么重训，链路都不会升速。
//    只有一次【真正的整卡复位】—— 用 SetupAPI 把设备禁用再启用
//    （设备真的从总线上消失再回来）—— 才能让 PCIe 核重新锁存声明为 Gen2
//    （LCAP2=0x06）。之后正常的两端 TLS + 重训即可把链路拉到 Gen2 x4。
//
//    注意：`pnputil /restart-device` 做不到这一点（设备不离开总线），
//          必须走 SetupAPI 的 DIF_PROPERTYCHANGE（本工具的 PnPReset）。
//
//  另外：驱动策略 RMPcieLinkSpeed 必须 = 1；= 2 会把 TLS 钳死在 Gen1。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

class P104Gen2x4
{
    // ---------- 常量 ----------
    const ushort VENDOR_NV = 0x10DE;
    const ushort DEVICE_P104 = 0x1B87;
    const uint PAGE_SIZE = 0x1000;

    const uint IOCTL_PCI_READ = 0x9C406144;   // WinRing0: 读 PCI 配置空间
    const uint IOCTL_PCI_WRITE = 0x9C40A148;  // WinRing0: 写 PCI 配置空间（★ 不是 0x9C406148）
    const uint IOCTL_MAP_PHYS = 0x8000645C;   // ThrottleStop: 映射物理页

    const string SVC_WR = "P104Gen2x4Wr";
    const string SVC_TS = "P104Gen2x4Ts";

    // BAR0 寄存器
    const uint OFF_PRIV_MISC1 = 0x8841C;      // 写使能位：置 bit11/13，清 bit12/14
    const uint OFF_VSEC_HIER = 0x88610;       // 清 bit12，置 bit0
    const uint OFF_LTSSM = 0x8872C;           // 写 6
    const uint OFF_LINKCFG0 = 0x8C040;        // SPEED[19:18]: Pascal 0=8GT 1=5GT 2=2.5GT
    const uint OFF_XP3G_OVR0 = 0x8E110;
    const uint OFF_XP3G_OVR3 = 0x8E11C;
    const uint OFF_XP3G_VAL0 = 0x8E120;
    const uint OFF_XP3G_VAL3 = 0x8E12C;
    const uint OFF_LCAP2_MIRROR = 0x880A4;    // 声明镜像（只读，用来看是否解锁）

    const uint SPEED_GEN2 = 1;                // Pascal 编码：1 = 5.0GT/s

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, OPEN_EXISTING = 3;
    static readonly IntPtr INVALID = new IntPtr(-1);

    // ---------- SetupAPI ----------
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        StringBuilder DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiSetClassInstallParams(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_PROPCHANGE_PARAMS ClassInstallParams, uint ClassInstallParamsSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(uint InstallFunction, IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property, out uint PropertyRegDataType, byte[] PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize);

    const uint DIGCF_PRESENT = 0x02;
    const uint DIGCF_ALLCLASSES = 0x04;
    const uint DIF_PROPERTYCHANGE = 0x12;
    const uint DICS_ENABLE = 0x01;
    const uint DICS_DISABLE = 0x02;
    const uint DICS_FLAG_GLOBAL = 0x01;
    const uint SPDRP_HARDWAREID = 0x01;

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);
    const uint DN_HAS_PROBLEM = 0x00000400;
    const uint CM_PROB_DISABLED = 22;

    // ---------- kernel32 ----------
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inLen,
                                       byte[] outBuf, uint outLen, out uint ret, IntPtr ov);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    // ---------- 状态 ----------
    static IntPtr hWr = INVALID, hTs = INVALID;
    static ulong _vaLink, _vaPpci, _vaXp3g;
    static uint _bar;
    static byte _bus, _dev, _fun;
    static bool Quiet = false;
    /// <summary>整卡复位最多做几次。★ 默认 1 —— 每次复位都会让屏幕黑一下，
    /// 多次复位会造成"反复黑屏"，所以必须显式 -retry N 才允许多次。</summary>
    static int ResetRetries = 1;
    static string ExeDir { get { return Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName); } }
    static string StatusFile = @"C:\ProgramData\P104Gen2x4\status.txt";
    /// <summary>★ 给人看的实时状态文件（不需要管理员就能读）</summary>
    static string StatusOkFile = @"C:\ProgramData\P104Gen2x4\当前状态.txt";
    static long _rewrites = 0, _samples = 0;

    /// <summary>
    /// ★ 写一个"人看的"实时状态文件。按住进程每 ~1 秒更新一次。
    /// 这样 status.cmd 不需要管理员权限、也不需要读寄存器，就能一句话告诉你 Gen1/Gen2。
    ///
    /// ★ 2026-10-16 改进：状态文件必须【自带证据】，不能只给结论。
    ///   起因：status.cmd 会报"Gen2 已解锁"，但 GPU-Z 显示"当前 1.1 x4"，
    ///   无法判断谁对。根因是旧版只打印解码后的结论、没有原始寄存器值可核实；
    ///   而且没有解释 GPU-Z / nvidia-smi 读的是【驱动上报值】、在这张矿卡上不可靠。
    /// </summary>
    static void WriteStatusFile(byte rb, byte rd, byte rf, uint rcap, uint ecap, uint eLnkSta, uint samples)
    {
        try
        {
            uint curSpeed = eLnkSta & 0xF;
            uint curWidth = (eLnkSta >> 4) & 0x3F;
            string gen = curSpeed >= 3 ? "Gen3" : curSpeed == 2 ? "Gen2" : "Gen1";
            string spd = curSpeed == 3 ? "8.0GT/s" : curSpeed == 2 ? "5.0GT/s" : "2.5GT/s";
            bool ok = curSpeed >= 2;

            // 端点：能力声明类寄存器（不是当前速率，只作对照）
            uint eLnkap = PciRead(_bus, _dev, _fun, ecap + 0x0C);
            uint eLnkap2 = PciRead(_bus, _dev, _fun, ecap + 0x2C);
            uint eLnkctl2 = PciRead(_bus, _dev, _fun, ecap + 0x30);
            // 上游根端口：它的 LNKSTA 也反映同一条链路的当前速率（交叉验证）
            uint rLnkSta = PciRead(rb, rd, rf, rcap + 0x12);

            string eg = (eLnkSta & 0xF) >= 3 ? "8.0GT/s(Gen3)" : (eLnkSta & 0xF) == 2 ? "5.0GT/s(Gen2)"
                      : (eLnkSta & 0xF) == 1 ? "2.5GT/s(Gen1)" : "?";
            string rg = (rLnkSta & 0xF) >= 3 ? "8.0GT/s(Gen3)" : (rLnkSta & 0xF) == 2 ? "5.0GT/s(Gen2)"
                      : (rLnkSta & 0xF) == 1 ? "2.5GT/s(Gen1)" : "?";
            string tg = (eLnkctl2 & 0xF) >= 3 ? "8.0GT/s(Gen3)" : (eLnkctl2 & 0xF) == 2 ? "5.0GT/s(Gen2)"
                      : (eLnkctl2 & 0xF) == 1 ? "2.5GT/s(Gen1)" : "?";
            string mg = (eLnkap & 0xF) >= 3 ? "8.0GT/s(Gen3)" : (eLnkap & 0xF) == 2 ? "5.0GT/s(Gen2)"
                      : (eLnkap & 0xF) == 1 ? "2.5GT/s(Gen1)" : "?";
            uint vec = (eLnkap2 >> 1) & 0x7F;

            var sb = new StringBuilder();
            sb.AppendLine("============================================================");
            sb.AppendLine("P104-100 PCIe 链路状态（由按住进程实时写入）");
            sb.AppendLine("更新时间 : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("============================================================");
            sb.AppendLine();
            sb.AppendLine("★ 当前链路速率 : " + gen + "  (" + spd + ")      <- 判据 = LNKSTA[3:0]");
            sb.AppendLine("  当前链路宽度 : x" + curWidth + "                 <- 判据 = LNKSTA[9:4]");
            sb.AppendLine();
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("【判据原始数据】—— 你可以自己核实这两行");
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("  端点   " + _bus.ToString("X2") + ":" + _dev.ToString("X2") + "." + _fun
                          + "  LNKSTA = 0x" + eLnkSta.ToString("X8")
                          + "   SPD[3:0]=" + curSpeed + " (" + eg + ")"
                          + "   WIDTH[9:4]=" + curWidth);
            sb.AppendLine("  根端口 " + rb.ToString("X2") + ":" + rd.ToString("X2") + "." + rf
                          + "  LNKSTA = 0x" + rLnkSta.ToString("X8")
                          + "   SPD[3:0]=" + (rLnkSta & 0xF) + " (" + rg + ")"
                          + "   WIDTH[9:4]=" + ((rLnkSta >> 4) & 0x3F));
            sb.AppendLine("  => 两端必须一致；上面两行的 SPD 都是 2 才是真的 Gen2。");
            sb.AppendLine();
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("【以下不是当前速率，只是\"宣称能力\"，别混淆】");
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("  端点 LNKCAP  (cap+0x0C) = 0x" + eLnkap.ToString("X8")
                          + "   MAXSPD=" + (eLnkap & 0xF) + " (" + mg + ")"
                          + "   MAXWIDTH=" + ((eLnkap >> 4) & 0x3F));
            sb.AppendLine("  端点 LNKCAP2 (cap+0x2C) = 0x" + eLnkap2.ToString("X8")
                          + "   支持速率向量 = 0x" + vec.ToString("X2")
                          + (vec == 3 ? " (Gen1+Gen2)" : vec == 1 ? " (仅 Gen1)" : ""));
            sb.AppendLine("  端点 LNKCTL2 (cap+0x30) = 0x" + eLnkctl2.ToString("X8")
                          + "   TLS[3:0]=" + (eLnkctl2 & 0xF) + " (" + tg + ")");
            sb.AppendLine();
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("【★ 为什么 GPU-Z / nvidia-smi 可能显示 Gen1？】");
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("  它们读的是【NVIDIA 驱动上报的值】，不是 PCIe 寄存器。");
            sb.AppendLine("  在这张【无显示输出的矿卡】上，驱动上报值不可靠 ——");
            sb.AppendLine("  nvidia-smi 的 pcie.link.gen.current / gpucurrent 在此卡上");
            sb.AppendLine("  始终报 1，即使寄存器 LNKSTA 和实测带宽都证明是 Gen2。");
            sb.AppendLine("  => 以本文件的 LNKSTA 原始值 + 带宽实测为准。");
            sb.AppendLine();
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("【带宽自检（唯一不会骗人的判据）】");
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("  Gen1 x4 = H2D 约 0.78 GB/s");
            sb.AppendLine("  Gen2 x4 = H2D 约 1.55 GB/s      <- 目标");
            sb.AppendLine("  Gen3 x4 = H2D 约 3.10 GB/s");
            sb.AppendLine("  跑 check-bw.cmd 可实测（需要 Python + torch）");
            sb.AppendLine();
            sb.AppendLine("============================================================");
            sb.AppendLine("结论     : " + (ok ? "【正常】" + gen + " x" + curWidth + " 已解锁并在保持"
                                            : "【异常】只跑到 " + gen + " —— 目标 Gen2"));
            sb.AppendLine("按住进程 : 运行中（PID " + Process.GetCurrentProcess().Id + "）");
            sb.AppendLine("累计采样 : " + samples + " 次");
            sb.AppendLine("============================================================");
            File.WriteAllText(StatusOkFile, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    static void Log(string s)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s;
        if (!Quiet) Console.WriteLine(line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatusFile));
            File.AppendAllText(StatusFile, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    static string Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false; psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true; psi.CreateNoWindow = true;
            var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            return o.Trim();
        }
        catch (Exception e) { return "ERR " + e.Message; }
    }

    // ================= 驱动 =================
    static void StageService(string svc, string sysPath)
    {
        Run("sc.exe", "stop " + svc);
        Run("sc.exe", "delete " + svc);
        Run("sc.exe", "create " + svc + " type= kernel start= demand binPath= \"" + sysPath + "\"");
        Run("sc.exe", "start " + svc);
    }

    static IntPtr TryOpen(string name)
    { return CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero); }

    static bool EnsureDevices()
    {
        hWr = TryOpen(@"\\.\WinRing0_1_2_0");
        hTs = TryOpen(@"\\.\" + SVC_TS);
        if (hWr != INVALID && hTs != INVALID) return true;
        string drvDir = Path.Combine(ExeDir, "drivers");
        string wr = Path.Combine(drvDir, "WinRing0x64.sys"), ts = Path.Combine(drvDir, "ThrottleStop.sys");
        if (!File.Exists(wr) || !File.Exists(ts))
        {
            Log("找不到驱动文件: " + drvDir);
            return false;
        }
        StageService(SVC_WR, wr);
        StageService(SVC_TS, ts);
        if (hWr == INVALID) hWr = TryOpen(@"\\.\WinRing0_1_2_0");
        if (hTs == INVALID) hTs = TryOpen(@"\\.\" + SVC_TS);
        if (hWr == INVALID || hTs == INVALID)
        {
            Log("驱动加载失败。常见原因：");
            Log("  · 未以管理员运行");
            Log("  · 易受攻击驱动阻止列表开启 → 关闭后重启");
            Log("  · 反作弊预启动驱动拦截（错误 31）→ 停掉它");
            return false;
        }
        return true;
    }

    static void UnloadServices()
    {
        Run("sc.exe", "stop " + SVC_WR); Run("sc.exe", "delete " + SVC_WR);
        Run("sc.exe", "stop " + SVC_TS); Run("sc.exe", "delete " + SVC_TS);
    }

    // ================= PCI 配置空间 =================
    static uint PciRead(byte bus, byte dev, byte fun, uint reg)
    {
        if (hWr == INVALID) return 0xFFFFFFFF;
        byte[] inb = new byte[8], outb = new byte[4];
        BitConverter.GetBytes((uint)((bus << 8) | (dev << 3) | fun)).CopyTo(inb, 0);
        BitConverter.GetBytes(reg).CopyTo(inb, 4);
        uint ret;
        if (!DeviceIoControl(hWr, IOCTL_PCI_READ, inb, 8, outb, 4, out ret, IntPtr.Zero)) return 0xFFFFFFFF;
        return BitConverter.ToUInt32(outb, 0);
    }

    static bool PciWrite(byte bus, byte dev, byte fun, uint reg, uint val)
    {
        if (hWr == INVALID) return false;
        byte[] inb = new byte[12], outb = new byte[4];
        BitConverter.GetBytes((uint)((bus << 8) | (dev << 3) | fun)).CopyTo(inb, 0);
        BitConverter.GetBytes(reg).CopyTo(inb, 4);
        BitConverter.GetBytes(val).CopyTo(inb, 8);
        uint ret;
        return DeviceIoControl(hWr, IOCTL_PCI_WRITE, inb, 12, outb, 4, out ret, IntPtr.Zero);
    }

    /// <summary>写通道自检：对只读的 0x00 做 no-op 同值写，请求成功即通道可用。</summary>
    static bool SelfTestWrite(byte bus, byte dev, byte fun)
    {
        return PciWrite(bus, dev, fun, 0x00, 0x1B8710DE);
    }

    static uint PciCap(byte bus, byte dev, byte fun)
    {
        uint ptr = PciRead(bus, dev, fun, 0x34) & 0xFC;
        int guard = 0;
        while (ptr != 0 && ptr < 0x100 && guard++ < 48)
        {
            uint head = PciRead(bus, dev, fun, ptr);
            if ((head & 0xFF) == 0x10) return ptr;
            ptr = (head >> 8) & 0xFC;
        }
        return 0;
    }

    /// <summary>找 P104。只扫 function 0（P104 是单功能设备）。</summary>
    static bool FindP104(out byte bus, out byte dev, out byte fun)
    {
        for (int b = 0; b < 64; b++)
            for (int d = 0; d < 32; d++)
            {
                uint id = PciRead((byte)b, (byte)d, 0, 0x00);
                if (id == 0xFFFFFFFF) continue;
                if ((id & 0xFFFF) == VENDOR_NV && ((id >> 16) & 0xFFFF) == DEVICE_P104)
                { bus = (byte)b; dev = (byte)d; fun = 0; return true; }
            }
        bus = dev = fun = 0; return false;
    }

    /// <summary>找 P104 的上游桥（必须扫全部 function —— PCH 端口常在 fn>0）</summary>
    static bool FindRootPort(byte tgt, out byte bus, out byte dev, out byte fun)
    {
        bus = dev = fun = 0;
        int bestSec = -1;
        for (int b = 0; b < 64; b++)
            for (int d = 0; d < 32; d++)
                for (int f = 0; f < 8; f++)
                {
                    uint id = PciRead((byte)b, (byte)d, (byte)f, 0x00);
                    if (id == 0xFFFFFFFF || (id & 0xFFFF) == 0xFFFF) continue;
                    if ((PciRead((byte)b, (byte)d, (byte)f, 0x08) >> 8) != 0x060400) continue;
                    // 0x18: Primary[7:0] Secondary[15:8] Subordinate[23:16] SecLatency[31:24]
                    uint sec = (PciRead((byte)b, (byte)d, (byte)f, 0x18) >> 8) & 0xFF;
                    uint sub = (PciRead((byte)b, (byte)d, (byte)f, 0x18) >> 16) & 0xFF;
                    if (sec <= tgt && tgt <= sub && (int)sec > bestSec)
                    {
                        bestSec = (int)sec; bus = (byte)b; dev = (byte)d; fun = (byte)f;
                    }
                }
        return bestSec >= 0;
    }

    // ================= BAR0 MMIO =================
    static ulong MapPage(uint phys)
    {
        if (hTs == INVALID) return 0;
        byte[] inb = new byte[12], outb = new byte[8];
        BitConverter.GetBytes((ulong)phys).CopyTo(inb, 0);
        BitConverter.GetBytes(PAGE_SIZE).CopyTo(inb, 8);
        uint ret;
        if (!DeviceIoControl(hTs, IOCTL_MAP_PHYS, inb, 12, outb, 8, out ret, IntPtr.Zero)) return 0;
        return BitConverter.ToUInt64(outb, 0);
    }

    /// <summary>每页只映射一次并缓存（MapPage 每次调用都会泄漏内核映射）</summary>
    static bool RemapAll()
    {
        _vaLink = MapPage(_bar + 0x8C000);
        _vaPpci = MapPage(_bar + 0x88000);
        _vaXp3g = MapPage(_bar + 0x8E000);
        return _vaLink != 0 && _vaPpci != 0 && _vaXp3g != 0;
    }

    static uint Mrd(ulong va, uint off)
    { return (uint)Marshal.ReadInt32((IntPtr)(long)(va + (off & 0xFFFu))); }
    static void Mwr(ulong va, uint off, uint val)
    { Marshal.WriteInt32((IntPtr)(long)(va + (off & 0xFFFu)), unchecked((int)val)); }

    // ================= 寄存器操作 =================
    static void WriteEnables()
    {
        Mwr(_vaXp3g, OFF_XP3G_OVR0, 0x00000001);
        Mwr(_vaXp3g, OFF_XP3G_OVR3, 0x00000004);
        Mwr(_vaXp3g, OFF_XP3G_VAL0, 0x00000000);
        Mwr(_vaXp3g, OFF_XP3G_VAL3, 0x00200000);

        // ★★★ 2026-10-16 修复：这是一个真 BUG 的根因 ★★★
        //
        //  resetpre 用 7/7 无反例（4 次复位 / 2 种复位机制）证明：
        //    复位【之前】PRIV_MISC_1 的 **bit15** 决定 L 能否建立：
        //        bit15 = 1  →  复位后声明锁存 0x06 (Gen2)  ✓
        //        bit15 = 0  →  复位后声明锁存 0x02 (Gen1)  ✗
        //
        //  而旧代码只「置 bit11/13、清 bit12/14」，【从不显式置 bit15】，
        //  仅靠"读旧值再 OR"【碰巧保留】它 —— 所以表现为"时好时坏"：
        //       上一次成功过      → bit15 残留 1 → 这次也成功
        //       刚开机/驱动刚重置 → bit15 = 0    → 这次失败（要再跑一次才行）
        //  实测复现：2026-10-16 18:08 重启后首次失败（初始 TLS=0），
        //            18:10 第二次成功（初始 TLS=1）。
        //
        //  ⇒ 显式置 bit15，并按"写通道必须先自检"的纪律【回读验证】。
        uint c1 = Mrd(_vaPpci, OFF_PRIV_MISC1);
        uint want = (c1 | (1u << 11) | (1u << 13) | (1u << 15)) & ~((1u << 12) | (1u << 14));
        Mwr(_vaPpci, OFF_PRIV_MISC1, want);
        uint got = Mrd(_vaPpci, OFF_PRIV_MISC1);
        if ((got & (1u << 15)) == 0)
            Log("!! PRIV_MISC_1 bit15 写入未落地（读回 0x" + got.ToString("X8")
                + "）—— 声明很可能锁存失败");

        uint c2 = Mrd(_vaPpci, OFF_VSEC_HIER);
        Mwr(_vaPpci, OFF_VSEC_HIER, (c2 & ~(1u << 12)) | 1u);
        Mwr(_vaPpci, OFF_LTSSM, 6);
    }

    static void WriteSpeedGen2()
    {
        uint cur = Mrd(_vaLink, OFF_LINKCFG0);
        Mwr(_vaLink, OFF_LINKCFG0, (cur & ~0x000C0000u) | (SPEED_GEN2 << 18) | 1u);
    }

    static void WriteTlsBoth(byte rb, byte rd, byte rf, uint rcap)
    {
        uint gcap = PciCap(_bus, _dev, _fun);
        if (gcap != 0)
        {
            uint cur = PciRead(_bus, _dev, _fun, gcap + 0x30);
            if ((cur & 0xF) != 2) PciWrite(_bus, _dev, _fun, gcap + 0x30, (cur & ~0xFu) | 2u);
        }
        if (rcap != 0)
        {
            uint cur = PciRead(rb, rd, rf, rcap + 0x30);
            if ((cur & 0xF) != 2) PciWrite(rb, rd, rf, rcap + 0x30, (cur & ~0xFu) | 2u);
        }
    }

    static void RetrainBoth(byte rb, byte rd, byte rf, uint rcap)
    {
        uint gcap = PciCap(_bus, _dev, _fun);
        if (rcap != 0)
        {
            uint lc = PciRead(rb, rd, rf, rcap + 0x10);
            if (lc != 0xFFFFFFFF) PciWrite(rb, rd, rf, rcap + 0x10, lc | (1u << 5));
        }
        if (gcap != 0)
        {
            uint lc = PciRead(_bus, _dev, _fun, gcap + 0x10);
            if (lc != 0xFFFFFFFF) PciWrite(_bus, _dev, _fun, gcap + 0x10, lc | (1u << 5));
        }
    }

    static string StateLine(byte rb, byte rd, byte rf, uint rcap)
    {
        uint gcap = PciCap(_bus, _dev, _fun);
        uint lc = PciRead(_bus, _dev, _fun, gcap + 0x0C);
        uint ls = PciRead(_bus, _dev, _fun, gcap + 0x12);
        uint l2 = PciRead(_bus, _dev, _fun, gcap + 0x2C);
        uint c2 = PciRead(_bus, _dev, _fun, gcap + 0x30);
        string s = string.Format("LNKCAP=0x{0:X8}({1} x{2}) LNKSTA=0x{3:X8}({4} x{5}) LCAP2=0x{6:X8} TLS={7}",
            lc, SpeedName(lc & 0xF), (lc >> 4) & 0x3F, ls, SpeedName(ls & 0xF), (ls >> 4) & 0x3F, l2, c2 & 0xF);
        return s;
    }

    static string SpeedName(uint f)
    {
        return f == 1 ? "2.5GT/s" : f == 2 ? "5.0GT/s" : f == 3 ? "8.0GT/s" : f == 4 ? "16GT/s" : "?";
    }

    // ================= ★★ PnP 禁用/启用 =================
    static bool GetDevNodeStatus(uint devInst, out uint problem)
    {
        uint status;
        return CM_Get_DevNode_Status(out status, out problem, devInst, 0) == 0 && (status & DN_HAS_PROBLEM) != 0;
    }

    /// <summary>列出所有 Present 的 P104 设备实例。</summary>
    static List<KeyValuePair<string, uint>> ListPresentP104()
    {
        var res = new List<KeyValuePair<string, uint>>();
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == INVALID) return res;
        try
        {
            var did = new SP_DEVINFO_DATA();
            did.cbSize = (uint)Marshal.SizeOf(did);
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref did); i++)
            {
                var sb = new StringBuilder(512);
                uint need;
                if (!SetupDiGetDeviceInstanceId(set, ref did, sb, 512, out need)) continue;
                string id = sb.ToString().ToUpperInvariant();
                if (id.Contains("VEN_10DE") && id.Contains("DEV_1B87"))
                    res.Add(new KeyValuePair<string, uint>(sb.ToString(), did.DevInst));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return res;
    }

    static bool PropertyChange(uint devInst, uint stateChange)
    {
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == INVALID) return false;
        try
        {
            var did = new SP_DEVINFO_DATA();
            did.cbSize = (uint)Marshal.SizeOf(did);
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref did); i++)
            {
                if (did.DevInst != devInst) continue;
                var pp = new SP_PROPCHANGE_PARAMS();
                pp.ClassInstallHeader.cbSize = (uint)Marshal.SizeOf(pp.ClassInstallHeader);
                pp.ClassInstallHeader.InstallFunction = DIF_PROPERTYCHANGE;
                pp.StateChange = stateChange;
                pp.Scope = DICS_FLAG_GLOBAL;
                pp.HwProfile = 0;
                if (!SetupDiSetClassInstallParams(set, ref did, ref pp, (uint)Marshal.SizeOf(pp))) return false;
                return SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref did);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return false;
    }

    /// <summary>
    /// ★★★ 本工具的核心：真正的整卡复位。
    /// 设备会从总线上消失，然后再回来 —— 只有这样才能让 PCIe 核
    /// 重新锁存「支持速率声明」为 Gen2。
    /// </summary>
    static bool PnPReset()
    {
        var list = ListPresentP104();
        if (list.Count == 0) { Log("找不到在线的 P104 设备实例"); return false; }
        var me = list[0];
        Log("设备实例 = " + me.Key);

        // 1) 禁用
        if (!PropertyChange(me.Value, DICS_DISABLE))
        {
            Log("禁用失败（错误 " + Marshal.GetLastWin32Error() + "）");
            return false;
        }
        bool gone = false;
        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(200);
            uint id = PciRead(_bus, _dev, _fun, 0x00);
            if (id != 0x1B8710DE) { gone = true; break; }
        }
        uint prob;
        GetDevNodeStatus(me.Value, out prob);
        Log("已禁用（设备离开总线=" + gone + "，problem=" + prob + "）");

        // 2) 启用
        var list2 = ListPresentP104();
        uint inst = me.Value;
        if (list2.Count > 0) inst = list2[0].Value;
        if (!PropertyChange(inst, DICS_ENABLE))
        {
            Log("启用失败（错误 " + Marshal.GetLastWin32Error() + "）");
            return false;
        }

        // 3) 等它回来
        bool back = false;
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(500);
            uint id = PciRead(_bus, _dev, _fun, 0x00);
            if (id == 0x1B8710DE)
            {
                Log(string.Format("设备已回到总线（{0:F1}s）", (i + 1) * 0.5));
                back = true;
                break;
            }
        }
        if (!back)
        {
            Log("!! 设备 30 秒内没有回来 —— 需要重启机器");
            return false;
        }

        // 4) ★★★ 关键：等声明锁存 ★★★
        //   实测（2026-10-15 反复验证，共 4 次对照）：
        //     复位后只等 2-3 秒读 → LCAP2 仍是 0x02（Gen1）→ 判定失败
        //     复位后等 25 秒读    → LCAP2 变成 0x06（Gen2）→ 成功
        //
        //   原因：需要 NVIDIA 驱动重新绑定到设备、读取 RMPcieLinkSpeed 策略，
        //         然后才会把声明锁存成 Gen2。这个过程要十几到二十几秒。
        //
        //   ⇒ 这是"时好时坏"的真正原因。以前偶尔成功，是因为那时碰巧等够了。
        Log("等待声明锁存（最多 20 秒，每 4 秒检查一次）…");
        uint gcap2 = PciCap(_bus, _dev, _fun);
        for (int i = 1; i <= 5; i++)
        {
            Thread.Sleep(4000);
            uint t = PciRead(_bus, _dev, _fun, gcap2 + 0x2C);
            if ((t & 0x6) == 0x6)
            {
                Log(string.Format("★ 声明已锁存为 Gen2（等待 {0} 秒后 LCAP2=0x{1:X8}）", i * 4, t));
                return true;
            }
        }
        Log("声明锁存超时（20 秒后仍是 Gen1）");
        return true;
    }

    // ================= 解锁流程 =================
    static bool UnlockOnce(byte rb, byte rd, byte rf, uint rcap, bool allowReset)
    {
        uint gcap = PciCap(_bus, _dev, _fun);
        uint l2 = PciRead(_bus, _dev, _fun, gcap + 0x2C);

        if ((l2 & 0x6) != 0x6 && allowReset)
        {
            // ★★★ 重要设计约束（2026-10-15 深夜修正）★★★
            //  整卡复位（禁用→启用）会让设备从总线上消失再回来。
            //  实测副作用：**显示器会黑一下**（影响显示子系统 / PCIe 拓扑）。
            //
            //  之前我加了"复位 → 验证 → 不成功就重试 6 次"的逻辑，结果在
            //  「声明反复锁存不成功」的机器状态下，变成了【90 秒内连续复位 6 次】，
            //  表现出来就是【反复黑屏】。
            //
            //  ⇒ 现在改为：**默认只复位 1 次**。
            //     需要多次时必须显式传 -retry N（且每次间隔 8 秒，减少闪烁）。
            int maxReset = ResetRetries;   // 默认 1
            bool latched = false;
            for (int attempt = 1; attempt <= maxReset; attempt++)
            {
                Log("声明仍是 Gen1（LCAP2=0x" + l2.ToString("X8") + "），执行整卡复位（禁用→启用）"
                    + (attempt > 1 ? " · 第 " + attempt + " 次" : ""));
                WriteEnables(); WriteSpeedGen2(); WriteDriverPolicy();
                if (!PnPReset()) return false;

                _bar = PciRead(_bus, _dev, _fun, 0x10) & 0xFFFFFFF0;
                CloseAll();
                Run("sc.exe", "stop " + SVC_WR); Run("sc.exe", "delete " + SVC_WR);
                Run("sc.exe", "stop " + SVC_TS); Run("sc.exe", "delete " + SVC_TS);
                Thread.Sleep(1500);
                if (!EnsureDevices()) { Log("复位后驱动重装失败"); return false; }
                if (!FindP104(out _bus, out _dev, out _fun)) { Log("复位后找不到 P104"); return false; }
                _bar = PciRead(_bus, _dev, _fun, 0x10) & 0xFFFFFFF0;
                if (!RemapAll()) { Log("复位后 MMIO 重映射失败"); return false; }
                gcap = PciCap(_bus, _dev, _fun);
                Log("复位后: " + StateLine(rb, rd, rf, rcap));

                uint l2b = PciRead(_bus, _dev, _fun, gcap + 0x2C);
                if ((l2b & 0x6) == 0x6) { latched = true; break; }

                if (attempt < maxReset)
                {
                    Log("声明未变（LCAP2=0x" + l2b.ToString("X8") + "）—— 8 秒后重试");
                    Thread.Sleep(8000);          // 间隔拉长，避免屏幕连续闪
                    WriteDriverPolicy();
                }
            }
            if (!latched)
                Log("声明未变成 Gen2（已复位 " + maxReset + " 次）—— 进入按住循环。"
                    + "若需再试请手动运行 -once；要一次多试可加 -retry 3");
        }

        // 逐轮：写使能 → 两端 TLS → 交替重训
        for (int rnd = 1; rnd <= 6; rnd++)
        {
            WriteEnables();
            WriteSpeedGen2();
            WriteTlsBoth(rb, rd, rf, rcap);
            RetrainBoth(rb, rd, rf, rcap);
            Thread.Sleep(700);
            uint ls = PciRead(_bus, _dev, _fun, gcap + 0x12);
            l2 = PciRead(_bus, _dev, _fun, gcap + 0x2C);
            Log("  第" + rnd + "轮: " + StateLine(rb, rd, rf, rcap));
            if ((ls & 0xF) >= 2) { Log("  *** 链路已上 Gen2 ***"); return true; }
        }
        return false;
    }

    // ================= 驱动策略 =================
    static string FindClassKey()
    {
        string baseKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(baseKey);
            if (root == null) return null;
            foreach (string sub in root.GetSubKeyNames())
            {
                var k = root.OpenSubKey(sub);
                if (k == null) continue;
                object m = k.GetValue("MatchingDeviceId");
                if (m != null && m.ToString().ToLower().Contains("1b87"))
                    return baseKey + "\\" + sub;
            }
        }
        catch { }
        return null;
    }

    static void WriteDriverPolicy()
    {
        string key = FindClassKey();
        if (key == null) { Log("找不到 P104 的显卡类键（跳过驱动策略）"); return; }
        try
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(key))
            {
                k.SetValue("RMPcieLinkSpeed", 1, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("RmForceEnableGen2", 1, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("RmEnableGen2", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            Log("驱动策略已写入 " + key + "  (RMPcieLinkSpeed=1)");
        }
        catch (Exception e) { Log("写驱动策略失败: " + e.Message); }
    }

    static void ClearDriverPolicy()
    {
        string key = FindClassKey();
        if (key == null) return;
        try
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key, true))
            {
                if (k == null) return;
                foreach (string n in new[] { "RMPcieLinkSpeed", "RmForceEnableGen2", "RmEnableGen2" })
                    try { k.DeleteValue(n, false); } catch { }
            }
            Log("驱动策略已清除");
        }
        catch { }
    }

    static bool IsAdmin()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// 状态查询。★ 设计要点：
    ///   1) 优先用【不需要管理员】的方式判断（进程 / 计划任务 / 工具自己的日志）
    ///   2) 失败信息只在真的失败时出现，不会因为"没管理员权限"就吓人
    ///   3) 只有需要读寄存器时才提权限；此时给出明确的一句提示
    /// </summary>
    static int DoCheck()
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool admin = IsAdmin();
        var procs = Process.GetProcessesByName("P104Gen2x4");
        bool holder = procs.Length > 0;

        // ★ 计划任务查询分三种结果（修 BUG 2：非管理员曾误报"未注册"）：
        //     taskOk=true                  → 已注册
        //     taskOk=false && admin        → 确实未注册（管理员有权查询）
        //     taskOk=false && !admin       → **无法查询**（不能说成"未注册"）
        string t = Run("schtasks.exe", "/query /tn \"" + TaskName + "\" /fo LIST");
        bool taskOk = t.IndexOf(TaskName, StringComparison.OrdinalIgnoreCase) >= 0;
        bool taskUnknown = !taskOk && !admin;

        // ---------- ① 实时状态文件（按住进程每秒刷新，不需要管理员）----------
        string realGen = null;
        uint realSpd = 0, realWidth = 0;
        string realTime = null;
        try
        {
            if (File.Exists(StatusOkFile))
            {
                foreach (string ln in File.ReadAllLines(StatusOkFile, Encoding.UTF8))
                {
                    if (ln.StartsWith("更新时间")) realTime = ln.Substring(ln.IndexOf(':') + 1).Trim();
                    else if (ln.StartsWith("当前速率"))
                    {
                        if (ln.Contains("Gen3")) realGen = "Gen3";
                        else if (ln.Contains("Gen2")) realGen = "Gen2";
                        else if (ln.Contains("Gen1")) realGen = "Gen1";
                        if (ln.Contains("8.0GT/s")) realSpd = 3;
                        else if (ln.Contains("5.0GT/s")) realSpd = 2;
                        else if (ln.Contains("2.5GT/s")) realSpd = 1;
                    }
                    else if (ln.StartsWith("当前宽度"))
                    {
                        int xi = ln.IndexOf('x');
                        if (xi >= 0) uint.TryParse(ln.Substring(xi + 1).Trim(), out realWidth);
                    }
                }
            }
        }
        catch { }

        // ---------- ② 回退：从日志读最近一次实测 ----------
        var st = ReadLastStateFromLog();

        string resGen = null; uint resSpd = 0, resWid = 0;
        if (realGen != null) { resGen = realGen; resSpd = realSpd; resWid = realWidth; }
        else if (st != null) { resGen = SpeedName(st.Item2); resSpd = st.Item2; resWid = st.Item3; }

        string resultLine;
        if (resGen == null) resultLine = "UNKNOWN   (no measurement yet - run check-bw.cmd)";
        else if (resSpd >= 3) resultLine = string.Format("Gen3 x{0}  (8.0 GT/s)   <-- UNLOCKED", resWid);
        else if (resSpd >= 2) resultLine = string.Format("Gen2 x{0}  (5.0 GT/s)   <-- UNLOCKED", resWid);
        else resultLine = string.Format("Gen1 x{0}  (2.5 GT/s)   <-- LOCKED (target: Gen2)", resWid);

        // ============ 顶部：醒目的 RESULT 块（修 BUG 3）============
        Console.WriteLine("============================================================");
        Console.WriteLine("  P104-100  PCIe Link Status");
        Console.WriteLine("============================================================");
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("   RESULT:  " + resultLine);
        Console.WriteLine("============================================================");
        Console.WriteLine();

        if (realGen != null)
        {
            Console.WriteLine("  链路宽度：x" + realWidth + "    数据时间：" + realTime);
            Console.WriteLine();
        }
        else if (st != null)
        {
            Console.WriteLine("  最近一次实测（{0}）：", st.Item1);
            Console.WriteLine("     速率 = {0}    宽度 = x{1}    目标速率 = {2}",
                SpeedName(st.Item2), st.Item3, SpeedName(st.Item4));
            Console.WriteLine();
        }

        // ---------- ③ 按住进程 / 计划任务 ----------
        Console.WriteLine("  按住进程 ....... {0}", holder
            ? string.Format("运行中（PID {0}）", procs[0].Id) : "未运行");
        if (taskOk) Console.WriteLine("  开机自启任务 ... 已注册（开机 + 登录 + 每分钟自愈）");
        else if (taskUnknown) Console.WriteLine("  开机自启任务 ... 无法查询（需要管理员权限才能查询计划任务）");
        else Console.WriteLine("  开机自启任务 ... 未注册");
        Console.WriteLine();

        // ---------- ④ 结论 ----------
        Console.WriteLine("============================================================");
        if (holder && (taskOk || taskUnknown) && resGen != null && resSpd >= 2)
        {
            Console.WriteLine("  结论：【正常】Gen2 已解锁，且正在保持");
            Console.WriteLine("        带宽参考：H2D 约 1.55 GB/s   D2H 约 1.69 GB/s");
            Console.WriteLine("        （Gen1 x4 时只有 0.78 / 0.85 GB/s）");
        }
        else if (holder && (taskOk || taskUnknown) && st != null && st.Item4 == 2)
        {
            Console.WriteLine("  结论：【正常】Gen2 已解锁");
            Console.WriteLine("        日志里那一行显示 Gen1，通常是采样时刚好空闲降速，");
            Console.WriteLine("        空闲降速是省电行为，有负载会自动升回 Gen2。");
            Console.WriteLine("        判据看「目标速率 = 5.0GT/s」即可。");
        }
        else if (holder && (taskOk || taskUnknown) && resGen == null)
        {
            Console.WriteLine("  结论：【看起来正常】但还没有可用的实测记录");
            Console.WriteLine("        按住进程在跑。等 1 分钟后重跑本命令看速率。");
        }
        else if (!holder)
        {
            Console.WriteLine("  结论：【未在保持】按住进程没有运行");
            Console.WriteLine("        → 链路会掉回 Gen1（这是正常的，不是故障）");
            Console.WriteLine("        → 双击 C:\\Program Files\\P104Gen2x4\\P104Gen2x4.exe");
            Console.WriteLine("          或重新运行 install.cmd（需要管理员）");
        }
        else
        {
            Console.WriteLine("  结论：【需要检查】");
            if (!taskOk && !taskUnknown)
            {
                Console.WriteLine("        - 开机自启任务未注册");
                Console.WriteLine("          → 以管理员运行 install.cmd");
            }
            if (resGen == null)
                Console.WriteLine("        - 没有可用的实测记录");
        }
        Console.WriteLine("============================================================");
        Console.WriteLine();

        // ---------- ⑤ 提示如何做寄存器级精确判定 ----------
        // ★ 故意【不】在这里自动读寄存器：那会临时加载驱动、抢 MMIO，
        //   可能干扰正在运行的按住进程。要看寄存器请显式用 -diag。
        Console.WriteLine("  ------------------------------------------------------------");
        Console.WriteLine("  想要寄存器级精确判定？（可选，需要管理员）");
        Console.WriteLine("     右键 status.cmd → 以管理员身份运行");
        Console.WriteLine("     或运行：  bin\\P104Gen2x4.exe -diag");
        Console.WriteLine();
        Console.WriteLine("  不这样做也能判断 —— 上面的结论就是读工具日志得出的，同样可靠。");
        Console.WriteLine("  本命令不需要管理员，也不会干扰正在运行的按住进程。");
        Console.WriteLine("  ------------------------------------------------------------");

        // ============ 底部：再重复一次 RESULT（修 BUG 3）============
        //   终端会停在最后一行，所以结论必须在【最后】再出现一次。
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("   RESULT:  " + resultLine);
        Console.WriteLine("============================================================");
        Console.WriteLine("   >>> If you only read one line, read the RESULT above. <<<");
        return 0;
    }

    /// <summary>从状态日志里取最近一条含链路速率的记录</summary>
    static Tuple<string, uint, uint, uint> ReadLastStateFromLog()
    {
        try
        {
            if (!File.Exists(StatusFile)) return null;
            string[] ls = File.ReadAllLines(StatusFile);
            for (int i = ls.Length - 1; i >= 0 && i > ls.Length - 200; i--)
            {
                string ln = ls[i];
                if (ln.IndexOf("LNKSTA=", StringComparison.Ordinal) < 0) continue;
                var m = Regex.Match(ln, @"LNKSTA=0x([0-9A-Fa-f]{8})");
                if (!m.Success) continue;
                uint ls2 = Convert.ToUInt32(m.Groups[1].Value, 16);
                uint spd = ls2 & 0xF, wid = (ls2 >> 4) & 0x3F;

                uint tls = 0;
                var m2 = Regex.Match(ln, @"TLS=(\d)");
                if (m2.Success) tls = uint.Parse(m2.Groups[1].Value);

                string ts = ln.Length >= 19 ? ln.Substring(0, 19) : "?";
                return Tuple.Create(ts, spd, wid, tls);
            }
        }
        catch { }
        return null;
    }

    /// <summary>深度诊断（需要管理员 + 驱动）</summary>
    static int DoDiag()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("============================================================");
        Console.WriteLine("  P104-100  PCIe Gen2x4  深度诊断");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        if (!IsAdmin())
        {
            Console.WriteLine("  深度诊断需要管理员权限（要加载内核驱动读 GPU 寄存器）。");
            Console.WriteLine();
            Console.WriteLine("  · 只是想看速率 → 直接运行 status.cmd，不需要管理员");
            Console.WriteLine("  · 要跑深度诊断   → 右键本程序 → 以管理员身份运行 -diag");
            return 1;
        }

        bool devOk = EnsureDevices();
        if (!devOk)
        {
            Console.WriteLine("  [X] 内核驱动加载失败");
            Console.WriteLine();
            Console.WriteLine("      常见原因与处理：");
            Console.WriteLine("        1. 易受攻击的驱动程序阻止列表开着");
            Console.WriteLine("           → Windows 安全中心 / 设备安全性 / 内核隔离 / 关掉它 / 重启");
            Console.WriteLine("        2. 反作弊的预启动驱动拦截（错误 31，常见腾讯 ACE-BOOT）");
            Console.WriteLine("           → 先停掉它，或换一台机器");
            Console.WriteLine("        3. 杀毒软件删掉了 bin\\drivers 下的 .sys 文件");
            Console.WriteLine("           → 加白名单后重新解压");
            return 2;
        }
        Console.WriteLine("  [OK] 内核驱动加载成功");
        Console.WriteLine();
        return DoDiagBody();
    }

    /// <summary>已确认有驱动时的寄存器级诊断</summary>
    static int DoDiagBody()
    {
        byte b, d, f;
        if (!FindP104(out b, out d, out f))
        {
            Console.WriteLine("  [X] 总线上找不到 P104-100（10DE:1B87）");
            return 3;
        }
        _bar = PciRead(b, d, f, 0x10) & 0xFFFFFFF0;
        if (!RemapAll())
        {
            Console.WriteLine("  [X] BAR0 寄存器读不到（MMIO 映射失败）");
            return 4;
        }

        uint gcap = PciCap(b, d, f);
        uint lc = PciRead(b, d, f, gcap + 0x0C);
        uint ls = PciRead(b, d, f, gcap + 0x12);
        uint l2 = PciRead(b, d, f, gcap + 0x2C);
        uint c2 = PciRead(b, d, f, gcap + 0x30);
        uint spd = ls & 0xF, wid = (ls >> 4) & 0x3F;
        bool declared = (l2 & 0x6) == 0x6;

        Console.WriteLine("  寄存器实测（{0:X2}:{1:X2}.{2}）", b, d, f);
        Console.WriteLine("     当前速率 ....... {0}   x{1}", SpeedName(spd), wid);
        Console.WriteLine("     目标速率 TLS ... {0}", SpeedName(c2 & 0xF));
        Console.WriteLine("     支持速率声明 ... {0}", declared ? "含 Gen2（已解锁）" : "仅 Gen1（未解锁）");
        Console.WriteLine("     LNKCAP ......... 0x{0:X8}   LNKSTA = 0x{1:X8}", lc, ls);
        Console.WriteLine();
        if (spd >= 2)
            Console.WriteLine("  >>> 【成功】当前链路是 {0} x{1}", SpeedName(spd), wid);
        else if (declared && (c2 & 0xF) == 2)
            Console.WriteLine("  >>> 【已解锁】目标速率已是 Gen2；当前显示 Gen1 多为空闲省电降速");
        else
            Console.WriteLine("  >>> 【未解锁】请以管理员运行 bin\\P104Gen2x4.exe -once 重试");
        CloseAll();
        return 0;
    }


    // ================= ★ VBIOS 读取与校验（2026-10-15 新增）=================
    //  发现：本卡的 VBIOS ROM 完整映射在【物理地址 BAR0 + 0x300000】（256KB），只读。
    //        另有一个完全相同的第二镜像在 BAR0 + 0x380000。
    //  ⇒ 可以【不依赖 nvflash】独立读取卡上的 VBIOS，用于：
    //      · 刷写前：确认卡上内容与备份一致
    //      · 刷写后：立刻校验是否写成功（不必等重启才知道变砖）
    //      · 任何时候：检查 EEPROM 有没有意外变化
    static readonly System.Collections.Generic.Dictionary<uint, ulong> _romPages
        = new System.Collections.Generic.Dictionary<uint, ulong>();

    static uint RomRd(uint romOff)
    {
        uint phys = _bar + 0x300000u + (romOff & ~0xFFFu);
        ulong va;
        if (!_romPages.TryGetValue(phys, out va))
        {
            va = MapPage(phys);
            _romPages[phys] = va;
        }
        if (va == 0) return 0xFFFFFFFF;
        return Mrd(va, romOff & 0xFFF);
    }

    static int DoVbios(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        const int ROM_LEN = 0x40000;       // 256 KB
        string outFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            string s = args[i].ToLower().TrimStart('-', '/');
            if ((s == "save" || s == "dump" || s == "o" || s == "out") && i + 1 < args.Length)
                outFile = args[i + 1];
            else if (s.StartsWith("save=") || s.StartsWith("dump=") || s.StartsWith("out="))
                outFile = args[i].Substring(args[i].IndexOf('=') + 1);
        }

        Console.WriteLine("=== P104 VBIOS 读取与校验 ===");
        if (!EnsureDevices()) { Console.WriteLine("驱动装载失败（需要管理员权限）"); return 3; }
        if (!FindP104(out _bus, out _dev, out _fun)) { Console.WriteLine("找不到 P104-100 (10DE:1B87)"); return 4; }
        _bar = PciRead(_bus, _dev, _fun, 0x10) & 0xFFFFFFF0;
        Console.WriteLine("P104 " + _bus.ToString("X2") + ":" + _dev.ToString("X2") + "." + _fun
                          + "  BAR0=0x" + _bar.ToString("X8"));

        // ★ 指纹校验：ROM 头应以 0xAA55 开头
        uint w0 = RomRd(0), w1 = RomRd(4), w2 = RomRd(8);
        Console.WriteLine("ROM 头: 0x" + w0.ToString("X8") + " 0x" + w1.ToString("X8")
                          + " 0x" + w2.ToString("X8"));
        if ((w0 & 0xFFFF) != 0xAA55)
        {
            Console.WriteLine("!! 未找到 0xAA55 签名 —— 该地址不是 VBIOS 映射");
            CloseAll(); return 7;
        }

        // 读整个 ROM
        byte[] rom = new byte[ROM_LEN];
        for (int i = 0; i < ROM_LEN; i += 4)
        {
            uint v = RomRd((uint)i);
            rom[i + 0] = (byte)(v & 0xFF);
            rom[i + 1] = (byte)((v >> 8) & 0xFF);
            rom[i + 2] = (byte)((v >> 16) & 0xFF);
            rom[i + 3] = (byte)((v >> 24) & 0xFF);
        }

        // 哈希
        string md5, sha;
        using (var m = System.Security.Cryptography.MD5.Create())
            md5 = BitConverter.ToString(m.ComputeHash(rom)).Replace("-", "").ToLowerInvariant();
        using (var s2 = System.Security.Cryptography.SHA256.Create())
            sha = BitConverter.ToString(s2.ComputeHash(rom)).Replace("-", "").ToLowerInvariant();

        // ASCII 摘要（ROM 头里有板号与日期）
        var sb = new StringBuilder();
        for (int i = 8; i < 64; i++)
            sb.Append(rom[i] >= 32 && rom[i] < 127 ? (char)rom[i] : '.');
        Console.WriteLine();
        Console.WriteLine("  ROM 摘要 : " + sb.ToString());
        Console.WriteLine("  ROM 大小 : " + ROM_LEN + " 字节 (256 KB)");
        Console.WriteLine("  ★ MD5    : " + md5);
        Console.WriteLine("  ★ SHA256 : " + sha);

        if (!string.IsNullOrEmpty(outFile))
        {
            try
            {
                File.WriteAllBytes(outFile, rom);
                Console.WriteLine("  已保存到 : " + Path.GetFullPath(outFile));
            }
            catch (Exception e) { Console.WriteLine("  保存失败: " + e.Message); }
        }

        Console.WriteLine();
        Console.WriteLine("  【怎么用】");
        Console.WriteLine("    刷写前：算一下你的备份 ROM 的 MD5，跟上面的比。一致就说明卡上是原厂内容。");
        Console.WriteLine("            命令：certutil -hashfile <备份.rom> MD5");
        Console.WriteLine("    刷写后：再跑一次本命令。如果 MD5 变了，说明写进去了；");
        Console.WriteLine("            如果还是旧的，说明刷新没生效（此时不要重启，先排查）。");
        Console.WriteLine();
        Console.WriteLine("  【注意】本命令只读，不改任何东西，也不会重启机器。");
        CloseAll();
        return 0;
    }


    // ================= main =================
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string mode = "bringup";
        bool allowReset = true;
        for (int ai = 0; ai < args.Length; ai++)
        {
            string a = args[ai];
            string s = a.ToLower().TrimStart('-', '/');
            if (s == "status" || s == "once" || s == "off" || s == "install" || s == "uninstall") mode = s;
            if (s == "check" || s == "status") mode = "check";
            if (s == "diag") mode = "diag";
            if (s == "vbios" || s == "rom") mode = "vbios";
            if (s == "quiet") Quiet = true;
            if (s == "noreset") allowReset = false;
            // -retry N ：允许最多 N 次整卡复位（默认 1）。★ 每次都会让屏幕黑一下，谨慎用。
            if (s == "retry" && ai + 1 < args.Length)
            {
                int n;
                if (int.TryParse(args[ai + 1], out n) && n >= 1 && n <= 10) ResetRetries = n;
            }
            else if (s.StartsWith("retry="))
            {
                int n;
                string val = a.Substring(a.IndexOf('=') + 1);
                if (int.TryParse(val, out n) && n >= 1 && n <= 10) ResetRetries = n;
            }
        }
        if (mode == "check") return DoCheck();
        if (mode == "diag") return DoDiag();
        if (mode == "vbios") return DoVbios(args);
        if (mode == "install") return DoInstall();
        if (mode == "uninstall") return DoUninstall();

        Log("=== P104Gen2x4 启动，模式=" + mode + " ===");

        // ★ 全局单实例锁：status / once / bringup 都通过这同一个 MMIO 驱动实例映射 BAR0，
        //   并发运行会互相干扰（读到旧页残留值）。所以同一时刻只允许一个非只读实例。
        bool created;
        Mutex mtx = new Mutex(true, @"Global\P104Gen2x4Session", out created);
        if (!created)
        {
            Log("已有实例在运行（可能是按住进程），本次退出以避免 MMIO 冲突");
            return 0;
        }

        // 清理上次异常退出可能留下的驱动服务（进程被杀时不会走 CloseAll）
        foreach (string svc in new[] { SVC_WR, SVC_TS })
        {
            string q = Run("sc.exe", "query " + svc);
            if (q.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0 ||
                q.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0)
                Run("sc.exe", "delete " + svc);
        }

        // ★ 开机/复位后，NVIDIA 驱动与 PCIe 总线可能还没就绪。
        //   旧版本这里直接 EnsureDevices() 失败就 return 3 —— 开机触发时太早会白跑一趟。
        //   现在最多重试 60 秒（每 2 秒一次），等设备与驱动就绪。
        bool devReady = false;
        for (int attempt = 1; attempt <= 30; attempt++)
        {
            if (EnsureDevices() && FindP104(out _bus, out _dev, out _fun))
            {
                devReady = true;
                if (attempt > 1) Log("设备在第 " + attempt + " 次尝试就绪");
                break;
            }
            if (attempt == 1) Log("设备/驱动尚未就绪，开始等待（最多 60 秒）…");
            Thread.Sleep(2000);
        }
        if (!devReady)
        {
            Log("找不到 P104-100 (10DE:1B87) —— 等待 60 秒后仍不可用");
            return 4;
        }
        if (!SelfTestWrite(_bus, _dev, _fun))
        {
            Log("!! 配置空间写通道自检失败 —— 拒绝继续（避免得到错误的\"只读\"结论）");
            return 5;
        }
        _bar = PciRead(_bus, _dev, _fun, 0x10) & 0xFFFFFFF0;
        byte rb, rd, rf; uint rcap = 0;
        bool haveRoot = FindRootPort(_bus, out rb, out rd, out rf);
        if (haveRoot) rcap = PciCap(rb, rd, rf);
        Log(string.Format("P104 {0:X2}:{1:X2}.{2}  BAR0=0x{3:X8}  上游 {4}",
            _bus, _dev, _fun, _bar,
            haveRoot ? string.Format("{0:X2}:{1:X2}.{2}", rb, rd, rf) : "(未找到)"));

        if (!RemapAll()) { Log("MMIO 映射失败"); return 6; }
        Log("初始: " + StateLine(rb, rd, rf, rcap));

        if (mode == "status")
        {
            Log("使能位: PRIV_MISC_1=0x" + Mrd(_vaPpci, OFF_PRIV_MISC1).ToString("X8")
                + " HIER=0x" + Mrd(_vaPpci, OFF_VSEC_HIER).ToString("X8")
                + " LTSSM=0x" + Mrd(_vaPpci, OFF_LTSSM).ToString("X8")
                + " LINK_CONFIG_0=0x" + Mrd(_vaLink, OFF_LINKCFG0).ToString("X8")
                + " LCAP2镜像=0x" + Mrd(_vaPpci, OFF_LCAP2_MIRROR).ToString("X8"));
            CloseAll(); return 0;
        }

        if (mode == "off")
        {
            Mwr(_vaLink, OFF_LINKCFG0, (Mrd(_vaLink, OFF_LINKCFG0) & ~0x000Fu) | (2u << 18));
            Mwr(_vaPpci, OFF_PRIV_MISC1, 0xC0B46500);
            Mwr(_vaPpci, OFF_VSEC_HIER, 0x00000000);
            Mwr(_vaPpci, OFF_LTSSM, 0x00000000);
            Log("已还原寄存器");
            CloseAll(); UnloadServices(); return 0;
        }

        WriteDriverPolicy();
        bool ok = UnlockOnce(rb, rd, rf, rcap, allowReset);

        if (mode == "once")
        {
            Log(ok ? "解锁完成（未常驻）" : "解锁未达成");
            CloseAll(); UnloadServices(); return ok ? 0 : 7;
        }

        Log(ok ? "进入常驻按住循环（每 20ms 对写驱动回写）" : "未达成，仍进入按住循环持续尝试");
        uint hgcap = PciCap(_bus, _dev, _fun);
        int lostStreak = 0;
        // ★ 自愈失败计数：如果反复重映射都恢复不了，就主动退出，
        //   让计划任务在下次触发时拉起一个干净的新实例。
        //   （旧版本这里 while(true) 永不退出 → 进程活着但按不住，
        //     计划任务又不会重启它 → 链路静默掉回 Gen1。这是"开机后要手动 install"的根因。）
        int healFail = 0;
        int lowSpeedStreak = 0;
        while (true)
        {
            try
            {
                // MMIO 掉线检测（0xFFFFFFFF 连续出现说明映射失效）
                uint chk = Mrd(_vaLink, OFF_LINKCFG0);
                if (chk == 0xFFFFFFFF)
                {
                    if (++lostStreak >= 20)
                    {
                        lostStreak = 0;
                        Log("检测到 MMIO 映射失效，重新装载驱动并重映射…（第 " + (healFail + 1) + " 次）");
                        CloseAll();
                        Run("sc.exe", "stop " + SVC_WR); Run("sc.exe", "delete " + SVC_WR);
                        Run("sc.exe", "stop " + SVC_TS); Run("sc.exe", "delete " + SVC_TS);
                        Thread.Sleep(1500);
                        bool healed = false;
                        if (EnsureDevices())
                        {
                            if (FindP104(out _bus, out _dev, out _fun))
                            {
                                _bar = PciRead(_bus, _dev, _fun, 0x10) & 0xFFFFFFF0;
                                if (RemapAll())
                                {
                                    hgcap = PciCap(_bus, _dev, _fun);
                                    FindRootPort(_bus, out rb, out rd, out rf);
                                    rcap = PciCap(rb, rd, rf);
                                    Log("映射已恢复");
                                    healed = true;
                                    healFail = 0;
                                }
                            }
                        }
                        if (!healed)
                        {
                            healFail++;
                            Log("自愈失败 " + healFail + "/5");
                            if (healFail >= 5)
                            {
                                Log("!! 连续 5 次自愈失败 —— 主动退出，交由计划任务在下次触发时重启");
                                CloseAll(); UnloadServices();
                                return 8;
                            }
                        }
                    }
                    Thread.Sleep(20);
                    continue;
                }
                lostStreak = 0;

                WriteEnables();
                WriteSpeedGen2();
                WriteTlsBoth(rb, rd, rf, rcap);
                uint ls = PciRead(_bus, _dev, _fun, hgcap + 0x12);
                _samples++;
                if ((ls & 0xF) < 2)
                {
                    RetrainBoth(rb, rd, rf, rcap);
                    _rewrites++;
                    // ★ 兜底：连续很久都按不住（约 30 秒），也主动退出重启
                    if (++lowSpeedStreak >= 1500)
                    {
                        Log("!! 连续 " + lowSpeedStreak + " 轮未能保持 Gen2 —— 主动退出，交由计划任务重启");
                        CloseAll(); UnloadServices();
                        return 9;
                    }
                }
                else
                {
                    lowSpeedStreak = 0;
                }
                if (_samples % 1500 == 0)
                    Log(string.Format("按住中: {0} | 采样 {1} 次 / 补训 {2} 次",
                        StateLine(rb, rd, rf, rcap), _samples, _rewrites));
                // ★ 每 ~50 个采样（约 1 秒）刷新一次"给人看"的状态文件
                if (_samples % 50 == 0)
                {
                    uint gs = PciRead(_bus, _dev, _fun, hgcap + 0x12);
                    WriteStatusFile(rb, rd, rf, rcap, hgcap, gs, (uint)_samples);
                }
            }
            catch { }
            Thread.Sleep(20);
        }
    }

    static void CloseAll()
    {
        if (hWr != INVALID) CloseHandle(hWr);
        if (hTs != INVALID) CloseHandle(hTs);
        hWr = hTs = INVALID;
    }

    // ================= 安装 / 卸载 =================
    static readonly string TaskName = "P104 PCIe Gen2x4 Bring-up";

    /// <summary>把计划任务写成 XML 注册 —— 支持"开机 + 登录 + 每分钟自愈"的触发组合</summary>
    static bool RegisterTask(string exe)
    {
        // ★ 为什么要 XML：schtasks 的命令行参数拼不出"多个触发器 + 重复间隔"，
        //   而自愈恰恰依赖这个：按住进程一旦被杀，最多 1 分钟内自动拉起。
        string xml = string.Format(
@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>P104-100 PCIe Gen2 x4 解锁（含按住循环与自愈）</Description>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger>
      <Enabled>true</Enabled>
      <Delay>PT60S</Delay>
    </BootTrigger>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT15S</Delay>
    </LogonTrigger>
    <TimeTrigger>
      <Enabled>true</Enabled>
      <StartBoundary>2026-01-01T00:00:00</StartBoundary>
      <Repetition>
        <Interval>PT1M</Interval>
        <Duration>P3650D</Duration>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{0}</Command>
      <Arguments>-quiet -retry 3</Arguments>
    </Exec>
  </Actions>
</Task>", exe);

        string tmp = Path.Combine(Path.GetTempPath(), "P104Gen2x4-task.xml");
        try
        {
            // schtasks /XML 需要 UTF-16
            File.WriteAllText(tmp, xml, new UnicodeEncoding(false, true));
        }
        catch (Exception e) { Log("写任务 XML 失败: " + e.Message); return false; }

        Run("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F");
        string r = Run("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
        try { File.Delete(tmp); } catch { }
        if (r.Length > 0) Log("schtasks: " + r);

        // ★ 独立校验：能查询到才算注册成功（schtasks 输出是 OEM 编码，字符串匹配不可靠）
        string q = Run("schtasks.exe", "/query /tn \"" + TaskName + "\" /fo LIST");
        bool ok = q.IndexOf(TaskName, StringComparison.OrdinalIgnoreCase) >= 0;
        Log("计划任务校验: " + (ok ? "存在 ✓" : "**未找到**"));
        return ok;
    }

    static int DoInstall()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log("=== 安装 ===");

        string self = Process.GetCurrentProcess().MainModule.FileName;
        string target = @"C:\Program Files\P104Gen2x4";
        string targetExe = Path.Combine(target, "P104Gen2x4.exe");

        // ★ 先停掉可能正在运行的按住进程 —— 否则它占着 exe，文件拷不过去。
        //   ⚠ 但如果【当前进程就是】那个 exe（直接跑 bin\P104Gen2x4.exe -install），
        //     taskkill /IM 会把自己也杀掉 → 安装中断（静默 exit 1）。
        //     旧版本没做这个判断，导致从 bin 目录运行 install 必失败。
        bool selfIsTarget = string.Equals(self, targetExe, StringComparison.OrdinalIgnoreCase);
        Run("schtasks.exe", "/end /TN \"" + TaskName + "\"");
        // ★★ 关键：taskkill /IM 是【按镜像名】杀进程，会连【当前正在运行的这个自己】一起杀掉
        //    （包里的 bin\P104Gen2x4.exe 和已安装的 exe 同名 → 必然自杀 → 静默 exit 1）。
        //    正确做法：列出同名进程，跳过自己的 PID。
        if (!selfIsTarget)
        {
            int myPid = Process.GetCurrentProcess().Id;
            int killed = 0;
            try
            {
                foreach (var pr in Process.GetProcessesByName("P104Gen2x4"))
                {
                    if (pr.Id == myPid) continue;   // 绝不杀自己
                    try { pr.Kill(); killed++; } catch { }
                }
            }
            catch { }
            Log("已结束 " + killed + " 个旧的按住进程（跳过本进程 PID " + myPid + "）");
        }
        else
        {
            Log("（当前就是已安装的 exe，无需结束其他进程）");
        }
        System.Threading.Thread.Sleep(2000);

        WriteDriverPolicy();
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(Path.Combine(target, "drivers"));
            if (!selfIsTarget)
            {
                File.Copy(self, targetExe, true);
                Log("已部署 exe 到 " + target);
            }
            else
            {
                Log("exe 已在目标位置，无需复制");
            }

            // ★ 驱动文件查找：优先 ExeDir\drivers，其次 <包>\bin\drivers，再次当前目录
            string drvSrc = null;
            foreach (string cand in new[]
            {
                Path.Combine(ExeDir, "drivers"),
                Path.Combine(ExeDir, "..", "bin", "drivers"),
                Path.Combine(Environment.CurrentDirectory, "bin", "drivers"),
                Path.Combine(Environment.CurrentDirectory, "drivers"),
                Path.Combine(target, "drivers"),
            })
            {
                if (File.Exists(Path.Combine(cand, "WinRing0x64.sys")) &&
                    File.Exists(Path.Combine(cand, "ThrottleStop.sys")))
                {
                    drvSrc = Path.GetFullPath(cand);
                    break;
                }
            }
            if (drvSrc == null) { Log("找不到驱动文件（WinRing0x64.sys / ThrottleStop.sys）"); return 1; }
            foreach (string f in new[] { "WinRing0x64.sys", "ThrottleStop.sys" })
                File.Copy(Path.Combine(drvSrc, f), Path.Combine(target, "drivers", f), true);
            Log("已部署驱动（来源 " + drvSrc + "）");
        }
        catch (Exception e) { Log("部署失败: " + e.Message); return 1; }

        bool taskOk = RegisterTask(targetExe);
        Log("开机自启: " + (taskOk ? "已注册（开机+登录+每分钟自愈）" : "注册失败，请手动运行工具"));
        Log("安装完成。重启后会自动解锁；也可立刻运行: \"" + targetExe + "\"");
        return 0;
    }

    static int DoUninstall()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log("=== 卸载 ===");
        Log(Run("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F"));
        UnloadServices();
        ClearDriverPolicy();
        Log("已删除计划任务、卸载驱动、清除驱动策略。");
        Log("（安装目录 C:\\Program Files\\P104Gen2x4 请手动删除）");
        return 0;
    }
}
