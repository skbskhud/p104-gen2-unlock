// ============================================================================
//  NVBIOSReader —— 通用的 NVIDIA 显卡 VBIOS 读取/校验工具
// ============================================================================
//  为什么需要它：
//    · GPU-Z 对某些矿卡（P104-100 / P106-100 / CMP 系列）保存不了 VBIOS
//    · 编程器（CH341A）需要拆机、接线，麻烦且危险
//    · nvflash 在某些机器上会导致重启
//
//  本工具直接通过 BYOVD 驱动读显卡的 ROM 映射窗口，因此：
//    · 不依赖任何厂商工具
//    · 不需要拆机
//    · 不会重启机器
//    · 全程只读
//
//  用法（管理员）：
//    NVBIOSReader.exe                       列出所有 NVIDIA 卡并读取 VBIOS
//    NVBIOSReader.exe -save <目录>          把每张卡的 VBIOS 存成文件
//    NVBIOSReader.exe -gui                  图形界面
//    NVBIOSReader.exe -verify <文件>        把卡上的 VBIOS 与给定文件对比
//
//  实测验证：P104-100 上读出的 MD5/SHA256 与 EEPROM 备份【完全一致】
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

class NVBIOSReader
{
    // ---------- WinRing0 ----------
    const uint IOCTL_PCI_READ = 0x9C406144;
    const uint IOCTL_PCI_WRITE = 0x9C40A148;
    const uint IOCTL_MAP_PHYS = 0x8000645C;      // ThrottleStop
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint OPEN_EXISTING = 3;
    static readonly IntPtr INVALID = new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa,
                                     uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inLen,
                                       byte[] outBuf, uint outLen, out uint ret, IntPtr ov);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    const string SVC_WR = "NVBIOSReaderWr";
    const string SVC_TS = "NVBIOSReaderTs";

    static IntPtr hWr = INVALID, hTs = INVALID;
    static readonly Dictionary<uint, ulong> pageCache = new Dictionary<uint, ulong>();
    /// <summary>本次运行复制到临时目录的驱动文件（退出时清理，避免留垃圾）</summary>
    static readonly List<string> tempFiles = new List<string>();

    // ================= 基础访问 =================
    static IntPtr TryOpen(string name)
    {
        IntPtr h = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero,
                               OPEN_EXISTING, 0, IntPtr.Zero);
        return h;
    }

    /// <summary>
    /// 打开设备；失败时重试几次。
    /// ★★★ 为什么需要重试：WinRing0 的设备名 \\.\WinRing0_1_2_0 是【写死】的，
    ///   系统里所有基于 WinRing0 的工具（例如 P104-Gen2x4 组合包）用的是【同一个】设备。
    ///   本工具以独占方式打开（share=0），所以对方正开着时这里就会失败。
    ///   重试几次可以容忍"对方刚好在关闭中"的短暂重叠。
    /// </summary>
    static IntPtr TryOpenWithRetry(string name, int attempts, int delayMs)
    {
        for (int i = 0; i < attempts; i++)
        {
            IntPtr h = TryOpen(name);
            if (h != INVALID) return h;
            if (i + 1 < attempts) Thread.Sleep(delayMs);
        }
        return INVALID;
    }

    /// <summary>exe 自身所在目录（★ 与"当前工作目录"无关，双击/命令行/计划任务都一样）</summary>
    public static string ExeDir
    {
        get
        {
            try
            {
                string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string d = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(d)) return d;
                }
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    /// <summary>
    /// 在若干个可能的位置找驱动文件。
    /// ★★★ 一律以【exe 自身所在目录】为基准解析，因此与"从哪里启动/当前目录是什么"无关。
    /// 支持这些布局（按顺序尝试）：
    ///     &lt;exe目录&gt;\bin\drivers\   ← 发布版布局（exe 在根，驱动收在 bin\ 里）
    ///     &lt;exe目录&gt;\drivers\       ← 旧布局
    ///     &lt;exe目录&gt;\               ← 驱动和 exe 放一起
    ///     &lt;exe目录&gt;\..\bin\drivers\ ← exe 自己就在 bin\ 里
    ///     &lt;exe目录&gt;\..\drivers\
    ///     &lt;exe目录&gt;\..
    /// </summary>
    public static string FindDriverFile(string fileName)
    {
        string d = ExeDir;
        string[] rel = {
            Path.Combine("bin", "drivers"),
            "drivers",
            "",
            Path.Combine("..", "bin", "drivers"),
            Path.Combine("..", "drivers"),
            "..",
        };
        foreach (string r in rel)
        {
            try
            {
                string p = Path.GetFullPath(Path.Combine(d, Path.Combine(r, fileName)));
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    static string Run(string exe, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return "";
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            return o;
        }
        catch { return ""; }
    }

    /// <summary>
    /// 准备一个可用的驱动。
    /// 实测要点（很重要）：
    ///   · WinRing0 的设备名 \\.\WinRing0_1_2_0 是写死在驱动里的
    ///   · ThrottleStop 的设备名【等于服务名】（IoCreateDeviceSecure + 动态名）
    ///   · 被 taskkill /F 强杀的进程会把驱动留在"设备在但 IOCTL 全失败"的坏状态，
    ///     而且此时 stop 会卡在 STOP_PENDING、start 报 1056 —— **只能重启机器恢复**
    ///   · 所以本工具【绝不强杀别的进程】，并优先复用已加载的驱动
    /// </summary>
    static bool StageService(string svc, string fileName)
    {
        try
        {
            // 先看服务是否已存在
            string q0 = Run("sc.exe", "query " + svc);
            bool exists = q0 != null && q0.IndexOf("SERVICE_NAME", StringComparison.OrdinalIgnoreCase) >= 0;

            if (exists)
            {
                // 已存在：如果在跑，直接用（不要 stop/start，避免卡 STOP_PENDING）
                if (q0.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                Run("sc.exe", "start " + svc);
                Thread.Sleep(500);
                return true;
            }

            // 不存在 → 需要 create（可能要管理员权限，某些机器会被策略拒绝）
            // ★ 用 FindDriverFile 找：优先 <exe目录>\bin\drivers\，其次 drivers\、exe 同目录
            string src = FindDriverFile(fileName);
            if (src == null) return false;

            string dst = Path.Combine(Path.GetTempPath(), "NVBIOSReader_" + fileName);
            try { File.Copy(src, dst, true); } catch { }
            if (!File.Exists(dst)) return false;
            lock (tempFiles) { if (!tempFiles.Contains(dst)) tempFiles.Add(dst); }

            Run("sc.exe", "create " + svc + " type= kernel start= demand binPath= \"" + dst + "\"");
            Thread.Sleep(200);
            Run("sc.exe", "start " + svc);
            Thread.Sleep(500);
            return true;
        }
        catch { return false; }
    }

    /// <summary>按候选顺序尝试打开一个"能用的"映射设备</summary>
    static bool OpenMapper(Action<string> L)
    {
        // ★ 优先用已经跑着的服务（不打扰它）
        string[] tsCandidates = { SVC_TS, "P104Gen2x4Ts", "P104LibTs", "NVBIOSReaderTs", "ThrottleStop" };
        foreach (string svc in tsCandidates)
        {
            IntPtr h = TryOpen(@"\\.\" + svc);
            if (h == INVALID) { L("  " + svc + " → 打不开"); continue; }
            // 验证真的能用
            if (ProbeMap(h))
            {
                hTs = h;
                L("  " + svc + " → ✓ 可用");
                return true;
            }
            L("  " + svc + " → 打开成功但 MapPhys 无效（驱动处于坏状态）");
            CloseHandle(h);
        }
        return false;
    }

    /// <summary>自检：试映射物理地址 0 的那一页，看是否返回非零 VA</summary>
    static bool ProbeMap(IntPtr h)
    {
        byte[] inb = new byte[12], outb = new byte[8];
        BitConverter.GetBytes(0UL).CopyTo(inb, 0);
        BitConverter.GetBytes(0x1000u).CopyTo(inb, 8);
        uint ret;
        if (!DeviceIoControl(h, IOCTL_MAP_PHYS, inb, 12, outb, 8, out ret, IntPtr.Zero)) return false;
        return BitConverter.ToUInt64(outb, 0) != 0;
    }

    public static bool OpenDrivers()
    {
        return OpenDrivers(null);
    }

    public static bool OpenDrivers(Action<string> dbg)
    {
        Action<string> L = s => { if (dbg != null) dbg(s); };

        // 1) WinRing0（PCI 配置空间）
        //   ★ 重试 3 次：别的 WinRing0 工具（如 P104-Gen2x4）可能正开着同一个设备
        hWr = TryOpenWithRetry(@"\\.\WinRing0_1_2_0", 3, 400);
        L("WinRing0: " + (hWr != INVALID ? "已加载 ✓" : "打不开（已重试 3 次）"));
        if (hWr == INVALID)
        {
            StageService(SVC_WR, "WinRing0x64.sys");
            hWr = TryOpenWithRetry(@"\\.\WinRing0_1_2_0", 3, 400);
            if (hWr == INVALID) hWr = TryOpenWithRetry(@"\\.\" + SVC_WR, 2, 300);
            L("WinRing0 重新装载后: " + (hWr != INVALID ? "✓" : "✗"));
        }

        // 2) 映射设备（ThrottleStop）—— 优先复用已有的
        L("查找可用的映射设备…");
        if (!OpenMapper(L))
        {
            L("现有设备都不可用，尝试装载自己的…");
            StageService(SVC_TS, "ThrottleStop.sys");
            hTs = TryOpenWithRetry(@"\\.\" + SVC_TS, 3, 400);
            if (hTs != INVALID && !ProbeMap(hTs)) { CloseHandle(hTs); hTs = INVALID; }
            L("自装载: " + (hTs != INVALID ? "✓" : "✗"));
        }

        if (hWr == INVALID || hTs == INVALID)
        {
            L("");
            L("!! 底层驱动不可用，无法读取显卡。");
            if (hWr == INVALID)
            {
                L("   · WinRing0（读 PCI 配置空间）打不开");
                L("     ★ 很可能是【其它工具正占用着】同一个底层驱动（例如 P104-Gen2x4 这类工具）");
                L("       → 请先把其它同类工具关掉，再重试本工具。");
            }
            if (hTs == INVALID) L("   · 内存映射驱动（ThrottleStop）不可用");
            L("   其它常见原因：");
            L("     ① 没有管理员权限");
            L("     ② 杀毒软件拦截了 bin\\drivers\\ 里的两个驱动（请加白名单）");
            L("     ③ 之前有程序被【强制结束】过 → 重启电脑后再试");
        }
        return hWr != INVALID && hTs != INVALID;
    }

    static void TryUnload(string svc)
    {
        Run("sc.exe", "stop " + svc);
        Run("sc.exe", "delete " + svc);
        Thread.Sleep(200);
    }

    public static void CloseDrivers()
    {
        if (hWr != INVALID) { CloseHandle(hWr); hWr = INVALID; }
        if (hTs != INVALID) { CloseHandle(hTs); hTs = INVALID; }
        // 只清理【本工具自己创建的两个服务】（名字带 NVBIOSReader 前缀，绝不会误删别人的）
        Run("sc.exe", "stop " + SVC_WR); Run("sc.exe", "delete " + SVC_WR);
        Run("sc.exe", "stop " + SVC_TS); Run("sc.exe", "delete " + SVC_TS);
        // 服务删掉之后，临时目录里的驱动副本才能删（否则文件被内核占用）
        Thread.Sleep(300);
        lock (tempFiles)
        {
            foreach (string f in tempFiles)
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
            tempFiles.Clear();
        }
    }

    public static uint PciRead(byte bus, byte dev, byte fun, uint reg)
    {
        if (hWr == INVALID) return 0xFFFFFFFF;
        byte[] inb = new byte[8], outb = new byte[4];
        BitConverter.GetBytes((uint)((bus << 8) | (dev << 3) | fun)).CopyTo(inb, 0);
        BitConverter.GetBytes(reg).CopyTo(inb, 4);
        uint ret;
        if (!DeviceIoControl(hWr, IOCTL_PCI_READ, inb, 8, outb, 4, out ret, IntPtr.Zero))
            return 0xFFFFFFFF;
        return BitConverter.ToUInt32(outb, 0);
    }

    public static ulong MapPage(uint phys)
    {
        if (hTs == INVALID) return 0;
        byte[] inb = new byte[12], outb = new byte[8];
        BitConverter.GetBytes((ulong)phys).CopyTo(inb, 0);
        BitConverter.GetBytes(0x1000u).CopyTo(inb, 8);
        uint ret;
        if (!DeviceIoControl(hTs, IOCTL_MAP_PHYS, inb, 12, outb, 8, out ret, IntPtr.Zero))
            return 0;
        return BitConverter.ToUInt64(outb, 0);
    }

    static unsafe uint Rd(ulong va, uint off)
    {
        if (va == 0) return 0xFFFFFFFF;
        return *(uint*)((byte*)va + off);
    }

    // ================= 显卡枚举 =================
    public class GpuInfo
    {
        public byte Bus, Dev, Fun;
        public uint DeviceId, SubsysId, Bar0, RomBase;
        public int RomWindowOff = -1;      // ROM 映射在 BAR0 里的偏移
        public string Name = "";
        public override string ToString()
        {
            return string.Format("{0:X2}:{1:X2}.{2}  10DE:{3:X4}  subsys:{4:X8}",
                Bus, Dev, Fun, DeviceId, SubsysId);
        }
    }

    /// <summary>扫描全部 PCI 总线，找出所有 NVIDIA 显示设备</summary>
    public static List<GpuInfo> FindNvidiaGpus()
    {
        var list = new List<GpuInfo>();
        for (int b = 0; b < 256; b++)
        {
            for (int d = 0; d < 32; d++)
            {
                uint id0 = PciRead((byte)b, (byte)d, 0, 0x00);
                if (id0 == 0xFFFFFFFF) continue;
                if ((id0 & 0xFFFF) != 0x10DE) continue;

                // 检查 function 0 是不是显示设备（class code 0x03xxxx）
                uint r08 = PciRead((byte)b, (byte)d, 0, 0x08);
                uint cls = (r08 >> 16) & 0xFFFF;
                if ((cls >> 8) != 0x03) continue;   // 只要显示类

                byte maxFun = 1;
                uint hdr = PciRead((byte)b, (byte)d, 0, 0x0C);
                if (((hdr >> 16) & 0x80) != 0) maxFun = 8;   // multi-function

                for (byte f = 0; f < maxFun; f++)
                {
                    uint id = PciRead((byte)b, (byte)d, f, 0x00);
                    if (id == 0xFFFFFFFF || (id & 0xFFFF) != 0x10DE) continue;
                    uint r = PciRead((byte)b, (byte)d, f, 0x08);
                    if (((r >> 16) & 0xFF00) != 0x0300) continue;

                    var g = new GpuInfo();
                    g.Bus = (byte)b; g.Dev = (byte)d; g.Fun = f;
                    g.DeviceId = (id >> 16) & 0xFFFF;
                    g.SubsysId = PciRead((byte)b, (byte)d, f, 0x2C);
                    g.Bar0 = PciRead((byte)b, (byte)d, f, 0x10) & 0xFFFFFFF0;
                    g.RomBase = PciRead((byte)b, (byte)d, f, 0x30) & 0xFFFFF800;
                    if (g.Bar0 == 0 || g.Bar0 == 0xFFFFFFF0) continue;
                    list.Add(g);
                }
            }
        }
        return list;
    }

    /// <summary>在 BAR0 里找 VBIOS 的 ROM 映射窗口（用 0xAA55 指纹 + 校验）</summary>
    public static int FindRomWindow(GpuInfo g)
    {
        // 常见窗口偏移优先
        var candidates = new List<uint>();
        foreach (uint o in new uint[] { 0x300000, 0x380000, 0x200000, 0x100000,
                                        0x400000, 0x480000, 0x500000, 0x600000 })
            candidates.Add(o);
        // 再补一些常见的
        for (uint o = 0x080000; o <= 0x800000; o += 0x80000)
            if (!candidates.Contains(o)) candidates.Add(o);

        foreach (uint off in candidates)
        {
            uint phys = g.Bar0 + off;
            uint page = phys & 0xFFFFF000;          // ★ 必须页对齐后再给驱动
            ulong va = MapPageRetry(page, 3);
            if (va == 0) continue;
            uint w0 = Rd(va, phys & 0xFFF);
            if ((w0 & 0xFFFF) != 0xAA55) continue;

            // 验证：第二个 dword 应该是合理的（非全 0 / 非全 F）
            uint w1 = Rd(va, (phys + 4) & 0xFFF);
            if (w1 == 0 || w1 == 0xFFFFFFFF) continue;

            // 再验证：偏移 0x1000 处不像哨兵
            uint phys2 = g.Bar0 + off + 0x1000;
            uint page2 = phys2 & 0xFFFFF000;
            ulong va2 = MapPageRetry(page2, 3);
            uint w2 = (va2 != 0) ? Rd(va2, phys2 & 0xFFF) : 0xFFFFFFFF;
            if (w2 == 0xFFFFFFFF) continue;

            return (int)off;
        }
        return -1;
    }

    /// <summary>最近一次 ReadRom 里"映射失败、读回 0xFFFFFFFF"的页数（0 = 完整）</summary>
    public static int LastReadBadPages = 0;

    /// <summary>
    /// 映射一页，失败自动重试。
    /// ★★★ 为什么必须重试：底层映射驱动会偶发瞬时失败（尤其被别的工具共用时）。
    ///   原实现把失败结果（0）缓存进 pageCache，于是那一页永远读回 0xFFFFFFFF，
    ///   最终【静默产出一张缺块的 VBIOS 备份】—— 这是发布前实测发现并修掉的真 BUG。
    /// </summary>
    static ulong MapPageRetry(uint page, int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            ulong va;
            if (pageCache.TryGetValue(page, out va) && va != 0) return va;
            pageCache.Remove(page);          // 不保留失败结果
            va = MapPage(page);
            if (va != 0) { pageCache[page] = va; return va; }
            Thread.Sleep(25);
        }
        return 0;
    }

    /// <summary>读一个卡的整个 VBIOS（★ 带完整性校验：失败页会重试，整图最多重读 3 轮，取失败页最少的一轮）</summary>
    public static byte[] ReadRom(GpuInfo g, int len, Action<int> progress)
    {
        if (g.RomWindowOff < 0) return null;

        byte[] best = null;
        int bestBad = int.MaxValue;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[] rom = new byte[len];
            var badPages = new HashSet<uint>();

            for (int i = 0; i < len; i += 4)
            {
                uint phys = g.Bar0 + (uint)g.RomWindowOff + (uint)i;
                uint page = phys & 0xFFFFF000;          // ★ 页对齐
                ulong va = MapPageRetry(page, 3);
                uint v;
                if (va != 0)
                {
                    v = Rd(va, phys & 0xFFF);
                }
                else
                {
                    v = 0xFFFFFFFF;
                    badPages.Add(page);
                }
                rom[i + 0] = (byte)(v & 0xFF);
                rom[i + 1] = (byte)((v >> 8) & 0xFF);
                rom[i + 2] = (byte)((v >> 16) & 0xFF);
                rom[i + 3] = (byte)((v >> 24) & 0xFF);
                if (progress != null && (i & 0x3FFF) == 0) progress(i * 100 / len);
            }

            if (badPages.Count < bestBad) { bestBad = badPages.Count; best = rom; }
            if (badPages.Count == 0) break;             // 完整，直接用

            // 有失败页 → 清掉页缓存，重头再读一轮（驱动可能已恢复）
            pageCache.Clear();
            Thread.Sleep(250);
        }

        LastReadBadPages = (bestBad == int.MaxValue) ? 0 : bestBad;
        if (progress != null) progress(100);
        return best;
    }

    /// <summary>从 ROM 头里猜容量（NVIDIA 用 0xAA55 + size_in_512b 的老格式；新卡固定 256KB）</summary>
    public static int GuessRomSize(byte[] probe)
    {
        if (probe == null || probe.Length < 0x1000) return 0x80000;
        // probe 至少 512KB。检测：向后找第一个"整片 0xFF"的 4KB 页
        for (int off = 0x10000; off <= 0x80000; off += 0x10000)
        {
            if (off + 0x1000 > probe.Length) break;   // ★ BUG 修复：原代码在 off=0x80000 时越界崩溃
            bool allFF = true;
            for (int i = 0; i < 0x1000; i += 4)
            {
                uint v = (uint)(probe[off + i] | (probe[off + i + 1] << 8) |
                                (probe[off + i + 2] << 16) | (probe[off + i + 3] << 24));
                if (v != 0xFFFFFFFF) { allFF = false; break; }
            }
            if (allFF) return off;
        }
        return 0x80000;
    }

    public static string Hex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (byte x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    public static string Md5(byte[] b)
    {
        using (var m = MD5.Create()) return Hex(m.ComputeHash(b));
    }

    public static string Sha256(byte[] b)
    {
        using (var s = SHA256.Create()) return Hex(s.ComputeHash(b));
    }

    public static string Ascii(byte[] b, int from, int len)
    {
        var sb = new StringBuilder();
        for (int i = from; i < from + len && i < b.Length; i++)
            sb.Append(b[i] >= 32 && b[i] < 127 ? (char)b[i] : '.');
        return sb.ToString();
    }

    public static bool IsAdmin()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
