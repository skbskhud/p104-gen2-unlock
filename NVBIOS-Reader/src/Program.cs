// ============================================================================
//  NVBIOSReader —— 主程序（命令行 + 图形界面）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Program
{
    const string VERSION = "1.2";

    [STAThread]
    static void Main(string[] args)
    {
        // ★★★ 关键：GUI 程序崩溃时默认什么都不显示（窗口一闪就没了）。
        //  必须挂全局异常处理，把错误写文件 + 弹窗，否则只会看到"打不开"。
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Fatal("未处理异常", e.ExceptionObject as Exception);
        Application.ThreadException += (s, e) => Fatal("界面线程异常", e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // ★★★ 没管理员权限就自动申请提权（exe 的清单里也声明了 requireAdministrator，这里是双保险）
        if (!NVBIOSReader.IsAdmin())
        {
            if (TryElevate(args)) return;
            MessageBox.Show(
                "本工具需要【管理员权限】才能读取显卡。\r\n\r\n" +
                "请先关闭本窗口，然后：右键 NVBIOSReader.exe → 以管理员身份运行。",
                "需要管理员权限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try { Start(args); }
        catch (Exception ex) { Fatal("启动失败", ex); }
    }

    /// <summary>不是管理员时，用 UAC 重新启动自己</summary>
    static bool TryElevate(string[] args)
    {
        try
        {
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = exe;
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            if (args != null && args.Length > 0)
            {
                var sb = new StringBuilder();
                foreach (string a in args)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                }
                psi.Arguments = sb.ToString();
            }
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch { return false; }     // 点了"否"，或系统不允许提权
    }

    static void Fatal(string title, Exception ex)
    {
        string msg = "【" + title + "】\r\n\r\n" + (ex != null ? ex.ToString() : "未知错误");
        try
        {
            string log = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "启动错误.log");
            File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg
                                    + "\r\n\r\n", Encoding.UTF8);
        }
        catch { }
        try
        {
            MessageBox.Show(msg + "\r\n\r\n（已写入 exe 同目录的 启动错误.log）", "NVBIOSReader 出错",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }

    static void Start(string[] args)
    {
        // ★★★ 注意：GUI 程序（/target:winexe）【没有控制台】。
        //  直接给 Console.OutputEncoding 赋值会抛 IOException("句柄无效")，
        //  导致窗口在创建前就崩掉 —— 看到的就是"双击没反应"。
        //  所以必须包 try/catch。
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        bool gui = args.Length == 0;
#if NVBIOS_CLI
        gui = false;      // ★ 命令行版：不带参数也走命令行（打印所有卡的 VBIOS 哈希），不弹 GUI
#endif
        bool save = false, verify = false, list = false;
        string outDir = null, verifyFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            string s = args[i].ToLower().TrimStart('-', '/');
            if (s == "gui") gui = true;
            else if (s == "cli" || s == "console") gui = false;
            else if (s == "save" || s == "dump")
            {
                save = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-")) outDir = args[++i];
            }
            else if (s == "verify" && i + 1 < args.Length) { verify = true; verifyFile = args[++i]; }
            else if (s == "list") list = true;
        }

        if (gui) { RunGui(); return; }
        RunCli(save, outDir, verify, verifyFile, list);
    }

    // ===================== 命令行模式 =====================
    static void RunCli(bool save, string outDir, bool verify, string verifyFile, bool list)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  NVBIOSReader v" + VERSION + " —— NVIDIA 显卡 VBIOS 读取/校验工具");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        if (!NVBIOSReader.IsAdmin())
        {
            Console.WriteLine("  !! 需要管理员权限。请右键 → 以管理员身份运行。");
            Console.WriteLine();
            Pause();
            return;
        }

        Console.WriteLine("  正在装载驱动…");
        if (!NVBIOSReader.OpenDrivers(s => Console.WriteLine("     [dbg] " + s)))
        {
            Console.WriteLine("  !! 驱动装载失败。请确认 bin\\drivers\\ 目录里有");
            Console.WriteLine("     WinRing0x64.sys 和 ThrottleStop.sys");
            Pause();
            return;
        }
        Console.WriteLine("  驱动就绪。");
        Console.WriteLine();

        Console.WriteLine("  正在扫描 PCI 总线上的 NVIDIA 显示设备…");
        var gpus = NVBIOSReader.FindNvidiaGpus();
        if (gpus.Count == 0)
        {
            Console.WriteLine("  未找到 NVIDIA 显示设备。");
            NVBIOSReader.CloseDrivers();
            Pause();
            return;
        }
        Console.WriteLine("  找到 " + gpus.Count + " 个 NVIDIA 显示设备：");
        Console.WriteLine();

        foreach (var g in gpus)
        {
            Console.WriteLine("  ── " + g.ToString());
            g.Name = "GPU_" + g.Bus.ToString("X2") + "_" + g.Dev.ToString("X2") + "_" + g.Fun;
            Console.WriteLine("     BAR0 = 0x" + g.Bar0.ToString("X8")
                              + "   ROM BAR = 0x" + g.RomBase.ToString("X8"));
            Console.Write("     查找 VBIOS 映射窗口… ");
            int off = NVBIOSReader.FindRomWindow(g);
            if (off < 0)
            {
                Console.WriteLine("未找到");
                continue;
            }
            g.RomWindowOff = off;
            Console.WriteLine("找到 (BAR0+0x" + off.ToString("X") + ")");

            Console.Write("     读取 VBIOS… ");
            byte[] rom = NVBIOSReader.ReadRom(g, 0x40000, null);
            if (rom == null) { Console.WriteLine("失败"); continue; }
            Console.WriteLine("完成");
            // ★ 完整性校验：底层映射偶发失败会让镜像缺块，必须明确告警，不能静默存成备份
            if (NVBIOSReader.LastReadBadPages > 0)
            {
                Console.WriteLine("     ⚠⚠ 警告: 有 " + NVBIOSReader.LastReadBadPages + " 页读取失败，这份镜像【可能不完整】，不要拿它当备份！");
                Console.WriteLine("         建议: 关掉其它占用底层驱动的工具后重试；或重启电脑后再读。");
            }

            Console.WriteLine("     摘要 : " + NVBIOSReader.Ascii(rom, 8, 56).TrimEnd('.'));
            Console.WriteLine("     大小 : " + rom.Length + " 字节");
            Console.WriteLine("     MD5  : " + NVBIOSReader.Md5(rom));
            Console.WriteLine("     SHA256: " + NVBIOSReader.Sha256(rom));

            if (save)
            {
                // ★ 不写 -save <目录> 时，默认存到 exe 同目录的 "VBIOS备份" 里。
                //   （不能用 Environment.CurrentDirectory —— 提权运行时它常常是 System32）
                string dir = outDir ?? Path.Combine(NVBIOSReader.ExeDir, "VBIOS备份");
                try
                {
                    Directory.CreateDirectory(dir);
                    string fn = Path.Combine(dir, g.Name + "_" +
                        g.DeviceId.ToString("X4") + "_" + NVBIOSReader.Md5(rom).Substring(0, 8) + ".rom");
                    File.WriteAllBytes(fn, rom);
                    Console.WriteLine("     已保存: " + fn);
                }
                catch (Exception e) { Console.WriteLine("     保存失败: " + e.Message); }
            }

            if (verify && verifyFile != null && File.Exists(verifyFile))
            {
                byte[] cmp = File.ReadAllBytes(verifyFile);
                Console.WriteLine();
                Console.WriteLine("     ── 与文件对比: " + Path.GetFileName(verifyFile));
                Console.WriteLine("        文件 MD5  : " + NVBIOSReader.Md5(cmp));
                Console.WriteLine("        卡上 MD5  : " + NVBIOSReader.Md5(rom));
                if (cmp.Length == rom.Length && NVBIOSReader.Md5(cmp) == NVBIOSReader.Md5(rom))
                    Console.WriteLine("        ★ 完全一致 —— 卡上内容与文件相同");
                else
                {
                    Console.WriteLine("        ★ 不一致！");
                    int n = Math.Min(cmp.Length, rom.Length), diff = 0;
                    for (int i = 0; i < n; i++) if (cmp[i] != rom[i]) diff++;
                    Console.WriteLine("          长度: 文件 " + cmp.Length + " / 卡上 " + rom.Length);
                    Console.WriteLine("          不同字节数: " + diff);
                }
            }
            Console.WriteLine();
        }

        NVBIOSReader.CloseDrivers();
        Console.WriteLine("  完成。本工具全程只读，没有修改任何东西。");
        Console.WriteLine();
        if (!save && !verify) Console.WriteLine("  提示：加 -save <目录> 可以把 VBIOS 存成文件。");
        Pause();
    }

    static void Pause()
    {
        Console.WriteLine("  按任意键退出…");
        try { Console.ReadKey(true); } catch { }
    }

    // ===================== 图形界面 =====================
    static Form form;
    static ListBox gpuList;
    static TextBox logBox;
    static Label statusLabel;
    static ProgressBar progress;
    static Button btnRefresh, btnReadOnly, btnSaveOne, btnSaveAll, btnCompare;

    static void RunGui()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        form = new Form();
        form.Text = "NVIDIA VBIOS 读取器 v" + VERSION + "   —   免拆机 / 免 nvflash / 全程只读";
        form.Size = new Size(760, 560);
        form.StartPosition = FormStartPosition.CenterScreen;
        form.MinimumSize = new Size(620, 460);
        form.Font = new Font("Microsoft YaHei UI", 9f);

        // 顶部：显卡列表
        var lblGpu = new Label();
        lblGpu.Text = "检测到的 NVIDIA 显示设备：";
        lblGpu.Location = new Point(12, 10);
        lblGpu.AutoSize = true;
        form.Controls.Add(lblGpu);

        gpuList = new ListBox();
        gpuList.Location = new Point(12, 32);
        gpuList.Size = new Size(720, 96);
        gpuList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        gpuList.HorizontalScrollbar = true;
        form.Controls.Add(gpuList);

        // 按钮行 1
        int y = 138;
        btnRefresh = MkButton("重新扫描", 12, y, 85, Refresh_Click);
        btnReadOnly = MkButton("读取（只看）", 103, y, 105, ReadOnly_Click);
        btnSaveOne = MkButton("读取并保存", 214, y, 105, SaveOne_Click);
        btnSaveAll = MkButton("保存全部", 325, y, 85, SaveAll_Click);
        btnCompare = MkButton("与文件对比…", 416, y, 110, Compare_Click);

        // 状态 + 进度
        statusLabel = new Label();
        statusLabel.Text = "就绪";
        statusLabel.Location = new Point(12, y + 36);
        statusLabel.Size = new Size(500, 20);
        statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        form.Controls.Add(statusLabel);

        progress = new ProgressBar();
        progress.Location = new Point(12, y + 58);
        progress.Size = new Size(720, 16);
        progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        form.Controls.Add(progress);

        // 日志
        var lblLog = new Label();
        lblLog.Text = "日志：";
        lblLog.Location = new Point(12, y + 80);
        lblLog.AutoSize = true;
        form.Controls.Add(lblLog);

        logBox = new TextBox();
        logBox.Location = new Point(12, y + 100);
        logBox.Size = new Size(720, 300);
        logBox.Multiline = true;
        logBox.ScrollBars = ScrollBars.Both;
        logBox.WordWrap = false;
        logBox.ReadOnly = true;
        logBox.BackColor = Color.FromArgb(250, 250, 250);
        logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        form.Controls.Add(logBox);

        form.Load += (s, e) => OnLoaded();
        form.FormClosing += (s, e) => { try { NVBIOSReader.CloseDrivers(); } catch { } };

        Application.Run(form);
    }

    static Button MkButton(string text, int x, int y, int w, EventHandler h)
    {
        var b = new Button();
        b.Text = text;
        b.Location = new Point(x, y);
        b.Size = new Size(w, 28);
        b.Click += h;
        form.Controls.Add(b);
        return b;
    }

    static readonly List<NVBIOSReader.GpuInfo> gpus = new List<NVBIOSReader.GpuInfo>();

    static void Log(string s)
    {
        if (logBox.InvokeRequired) { logBox.BeginInvoke(new Action<string>(Log), s); return; }
        logBox.AppendText(s + "\r\n");
    }

    static void SetStatus(string s)
    {
        if (statusLabel.InvokeRequired) { statusLabel.BeginInvoke(new Action<string>(SetStatus), s); return; }
        statusLabel.Text = s;
    }

    static void SetBusy(bool b)
    {
        btnRefresh.Enabled = !b;
        btnReadOnly.Enabled = !b;
        btnSaveOne.Enabled = !b;
        btnSaveAll.Enabled = !b;
        btnCompare.Enabled = !b;
        Application.DoEvents();
    }

    static void OnLoaded()
    {
        if (!NVBIOSReader.IsAdmin())
        {
            Log("!! 没有管理员权限，无法读取显卡。");
            Log("   请关闭本窗口，右键 NVBIOSReader.exe → 以管理员身份运行。");
            SetStatus("权限不足");
            SetBusy(true);
            MessageBox.Show("本工具需要【管理员权限】。\r\n\r\n" +
                            "请关闭本窗口，然后：右键 NVBIOSReader.exe → 以管理员身份运行。",
                            "需要管理员权限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Log("管理员权限：OK");
        Log("正在装载驱动…");
        // ★ BUG 修复：原来这里把驱动日志写到 Console.WriteLine —— GUI 程序没有控制台，
        //   等于把最关键的诊断信息丢掉了。改成写进界面日志框。
        if (!NVBIOSReader.OpenDrivers(s => Log("   [驱动] " + s)))
        {
            Log("");
            Log("!! 驱动装载失败 —— 无法继续。");
            Log("   ① 请确认 bin\\drivers\\ 里有 WinRing0x64.sys 和 ThrottleStop.sys");
            Log("   ② 杀毒软件可能拦截了这两个驱动，请加白名单");
            Log("   ③ 如果之前有程序被【强制结束】过，重启电脑再试");
            SetStatus("驱动装载失败");
            SetBusy(true);
            MessageBox.Show("驱动装载失败，无法读取显卡。\r\n\r\n" +
                            "① 确认 bin\\drivers\\ 里有 WinRing0x64.sys 和 ThrottleStop.sys\r\n" +
                            "② 杀毒软件可能拦截了驱动，请加白名单\r\n" +
                            "③ 如果之前有程序被强制结束过，重启电脑再试",
                            "驱动装载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Log("驱动就绪");
        Log("");
        Refresh_Click(null, null);
    }

    static void Refresh_Click(object sender, EventArgs e)
    {
        SetBusy(true);
        gpus.Clear();
        gpuList.Items.Clear();
        // ★ BUG 修复：原来这里有 logBox.Clear()，会把前面"驱动装载"的诊断日志整段抹掉 ——
        //   新手一旦遇到问题，看到的日志里就【没有原因】了。改成只加一条分隔线。
        Log("");
        Log("──────── 扫描 ────────");

        Log("正在扫描 PCI 总线上的 NVIDIA 显示设备…");
        var found = NVBIOSReader.FindNvidiaGpus();
        Log("找到 " + found.Count + " 个设备");
        Log("");

        foreach (var g in found)
        {
            g.Name = "GPU_" + g.Bus.ToString("X2") + "_" + g.Dev.ToString("X2") + "_" + g.Fun;
            Log("── " + g.ToString());
            Log("   BAR0 = 0x" + g.Bar0.ToString("X8") + "   ROM BAR = 0x" + g.RomBase.ToString("X8"));
            SetStatus("查找 " + g.Name + " 的 VBIOS 窗口…");
            int off = NVBIOSReader.FindRomWindow(g);
            if (off < 0)
            {
                Log("   VBIOS 映射窗口：未找到");
                gpuList.Items.Add(g.Name + "   [未找到 VBIOS 窗口]");
                Log("");
                continue;
            }
            g.RomWindowOff = off;
            Log("   VBIOS 映射窗口：BAR0 + 0x" + off.ToString("X"));
            gpus.Add(g);
            gpuList.Items.Add(g.Name + "   [0x" + g.DeviceId.ToString("X4") + "]   窗口 BAR0+0x" + off.ToString("X"));
            Log("");
        }

        if (gpus.Count > 0) gpuList.SelectedIndex = 0;
        SetStatus(gpus.Count > 0 ? ("就绪 —— 找到 " + gpus.Count + " 张可读取的卡")
                                 : "没有找到可读取的卡");
        SetBusy(false);
    }

    static NVBIOSReader.GpuInfo Selected()
    {
        int i = gpuList.SelectedIndex;
        if (i < 0 || i >= gpus.Count)
        {
            Log("请先在列表里选择一张卡。");
            return null;
        }
        return gpus[i];
    }

    static byte[] ReadWithProgress(NVBIOSReader.GpuInfo g)
    {
        SetStatus("读取 " + g.Name + " 的 VBIOS…");
        progress.Value = 0;
        byte[] rom = NVBIOSReader.ReadRom(g, 0x40000, p =>
        {
            if (progress.InvokeRequired) progress.BeginInvoke(new Action<int>(v => progress.Value = v), p);
            else progress.Value = p;
        });
        progress.Value = 100;
        // ★ 完整性校验告警（读取可能因驱动瞬时失败而缺块）
        if (rom != null && NVBIOSReader.LastReadBadPages > 0)
        {
            Log("");
            Log("   ⚠⚠ 警告：有 " + NVBIOSReader.LastReadBadPages + " 页读取失败，这份镜像【可能不完整】！");
            Log("      请不要拿它当备份；建议关掉其它占用底层驱动的工具后重试，或重启电脑后再读。");
            SetStatus("⚠ 读取不完整（" + NVBIOSReader.LastReadBadPages + " 页失败）");
        }
        return rom;
    }

    static void ShowRomInfo(NVBIOSReader.GpuInfo g, byte[] rom)
    {
        Log("   摘要   : " + NVBIOSReader.Ascii(rom, 8, 56).TrimEnd('.'));
        Log("   大小   : " + rom.Length + " 字节");
        Log("   ★ MD5  : " + NVBIOSReader.Md5(rom));
        Log("   ★ SHA256: " + NVBIOSReader.Sha256(rom));
    }

    static string SaveRom(NVBIOSReader.GpuInfo g, byte[] rom)
    {
        using (var dlg = new SaveFileDialog())
        {
            dlg.Title = "保存 " + g.Name + " 的 VBIOS";
            dlg.Filter = "VBIOS 镜像 (*.rom)|*.rom|所有文件 (*.*)|*.*";
            dlg.FileName = g.Name + "_" + g.DeviceId.ToString("X4") + "_" +
                           NVBIOSReader.Md5(rom).Substring(0, 8) + ".rom";
            if (dlg.ShowDialog() != DialogResult.OK) return null;
            File.WriteAllBytes(dlg.FileName, rom);
            return dlg.FileName;
        }
    }

    /// <summary>★ 新增：只读取、不保存 —— 新手最常用的动作（点一下就看到 MD5，不用选保存位置）</summary>
    static void ReadOnly_Click(object sender, EventArgs e)
    {
        var g = Selected(); if (g == null) return;
        SetBusy(true);
        Log("── 读取 " + g.Name);
        byte[] rom = ReadWithProgress(g);
        if (rom == null) { Log("   读取失败"); SetStatus("读取失败"); SetBusy(false); return; }
        ShowRomInfo(g, rom);
        Log("");
        SetStatus("读取完成（只读，未做任何修改）");
        SetBusy(false);
        MessageBox.Show("读取成功！\r\n\r\n" +
                        "显卡：" + g.Name + "\r\n" +
                        "大小：" + rom.Length + " 字节\r\n" +
                        "MD5：" + NVBIOSReader.Md5(rom) + "\r\n\r\n" +
                        "（本工具全程只读，没有对显卡做任何修改）",
                        "读取成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static void SaveOne_Click(object sender, EventArgs e)
    {
        var g = Selected(); if (g == null) return;
        SetBusy(true);
        Log("── 读取 " + g.Name);
        byte[] rom = ReadWithProgress(g);
        if (rom == null) { Log("   读取失败"); SetBusy(false); return; }
        ShowRomInfo(g, rom);
        string fn = SaveRom(g, rom);
        if (fn != null) { Log("   已保存: " + fn); SetStatus("已保存 " + Path.GetFileName(fn)); }
        else SetStatus("已取消保存");
        Log("");
        SetBusy(false);
    }

    static void SaveAll_Click(object sender, EventArgs e)
    {
        if (gpus.Count == 0) { Log("没有可保存的卡。"); return; }
        using (var dlg = new FolderBrowserDialog())
        {
            dlg.Description = "选择保存目录";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            SetBusy(true);
            foreach (var g in gpus)
            {
                Log("── 读取 " + g.Name);
                byte[] rom = ReadWithProgress(g);
                if (rom == null) { Log("   读取失败"); continue; }
                ShowRomInfo(g, rom);
                string fn = Path.Combine(dlg.SelectedPath,
                    g.Name + "_" + g.DeviceId.ToString("X4") + "_" +
                    NVBIOSReader.Md5(rom).Substring(0, 8) + ".rom");
                try { File.WriteAllBytes(fn, rom); Log("   已保存: " + fn); }
                catch (Exception ex) { Log("   保存失败: " + ex.Message); }
                Log("");
            }
            SetStatus("全部完成");
            SetBusy(false);
        }
    }

    static void Compare_Click(object sender, EventArgs e)
    {
        var g = Selected(); if (g == null) return;
        string file;
        using (var dlg = new OpenFileDialog())
        {
            dlg.Title = "选择要比对的 VBIOS 文件";
            dlg.Filter = "VBIOS 镜像 (*.rom;*.bin)|*.rom;*.bin|所有文件 (*.*)|*.*";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            file = dlg.FileName;
        }
        SetBusy(true);
        Log("── 对比 " + g.Name + "  与  " + Path.GetFileName(file));
        byte[] rom = ReadWithProgress(g);
        if (rom == null) { Log("   读取失败"); SetBusy(false); return; }
        byte[] cmp;
        try { cmp = File.ReadAllBytes(file); }
        catch (Exception ex) { Log("   读文件失败: " + ex.Message); SetBusy(false); return; }

        string m1 = NVBIOSReader.Md5(rom), m2 = NVBIOSReader.Md5(cmp);
        Log("   卡上 MD5 : " + m1);
        Log("   文件 MD5 : " + m2);
        Log("   卡上大小 : " + rom.Length + "   文件大小 : " + cmp.Length);
        if (m1 == m2)
        {
            Log("   ★★★ 完全一致 —— 卡上内容与文件相同");
            SetStatus("对比结果：一致");
            MessageBox.Show("完全一致！\n\n卡上的 VBIOS 与你选择的文件相同。",
                            "对比结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            int n = Math.Min(cmp.Length, rom.Length), diff = 0;
            for (int i = 0; i < n; i++) if (cmp[i] != rom[i]) diff++;
            Log("   ★ 不一致 —— 不同字节数 " + diff);
            SetStatus("对比结果：不一致");
            MessageBox.Show("不一致！\n\n不同字节数：" + diff +
                            "\n\n（如果是刷写后对比：说明刷新没生效，先别重启，先排查）",
                            "对比结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Log("");
        SetBusy(false);
    }
}
