# -*- coding: utf-8 -*-
"""
读卡参数 —— 通用只读脚本（P104 / P106 / 任何 NVIDIA 卡都能跑）

★ 纯只读：不写任何寄存器、不复位、不改驱动
★ 不管插在 x1 槽还是 x16 槽都能跑（会自动记录当前训练宽度作为参考）
★ 输出一个 dump 文件，用于离线差分对比

用法：
    python dump_card.py [输出文件前缀]

依赖：_scratch/lib_session.py（本项目自带）
"""
import sys, os, time, json

sys.path.insert(0, r"D:\workplace2\dsh-workspace\_scratch")

OUT_PREFIX = sys.argv[1] if len(sys.argv) > 1 else "dump"

# ---------------------------------------------------------------- 工具
def sentinel(v):
    """判断是否是哨兵/毒值"""
    if v is None:
        return "READ_FAIL"
    if (v & 0xFFFF0000) == 0xBADF0000 or (v & 0xFFFFFF00) == 0xBADF5000:
        return "POISON(0xBADF****)"
    if (v & 0xFFFF0000) == 0xDEAD0000:
        return "SENTINEL(0xDEAD****)"
    if v == 0xFFFFFFFF:
        return "ALL_ONES"
    return ""

def spd_name(x):
    return {1: "2.5GT/s(Gen1)", 2: "5.0GT/s(Gen2)", 3: "8.0GT/s(Gen3)", 4: "16GT/s(Gen4)"}.get(x, "?%d" % x)

# ---------------------------------------------------------------- 主流程
def main():
    from lib_session import get_session

    s = get_session(verbose=False)
    if s is None:
        print("X 拿不到 session（可能是 BYOVD 驱动没起来，或别的进程占着 WinRing0）")
        print("  提示：先禁用计划任务、停掉常驻进程，再试")
        sys.exit(1)

    b, d, f = s.loc
    lines = []
    def P(t=""):
        lines.append(t)
        print(t)

    P("=" * 78)
    P("显卡参数 dump（只读）  %s" % time.strftime("%Y-%m-%d %H:%M:%S"))
    P("=" * 78)
    P()

    # ---- 设备标识
    P("【1】设备标识")
    vid_did = s.prd(b, d, f, 0x00)
    rev_class = s.prd(b, d, f, 0x08)
    subsys = s.prd(b, d, f, 0x2C)
    if vid_did is not None:
        P("  PCI %02X:%02X.%d   VENDOR=%04X  DEVICE=%04X   (CFG0x00=0x%08X)"
          % (b, d, f, vid_did & 0xFFFF, (vid_did >> 16) & 0xFFFF, vid_did))
    if rev_class is not None:
        P("  REVISION=0x%02X  CLASS=0x%06X" % (rev_class & 0xFF, (rev_class >> 8) & 0xFFFFFF))
    if subsys is not None:
        P("  SUBSYSTEM: VENDOR=%04X  ID=%04X" % (subsys & 0xFFFF, (subsys >> 16) & 0xFFFF))
    P()

    # ---- PCIe 声明（★ 最关键）
    P("【2】★★★ PCIe 速率/宽度声明（这是对比的核心）")
    cap = s.pcie_cap(b, d, f)
    if cap:
        lnkcap = s.prd(b, d, f, cap + 0x0C)
        lnksta = s.prd(b, d, f, cap + 0x12)
        lnkcap2 = s.prd(b, d, f, cap + 0x2C)
        lnkctl2 = s.prd(b, d, f, cap + 0x30)
        devcap = s.prd(b, d, f, cap + 0x04)
        devctl = s.prd(b, d, f, cap + 0x08)
        devsta = s.prd(b, d, f, cap + 0x0A)
        P("  PCIe cap @ 0x%02X" % cap)
        if lnkcap is not None:
            P("  ★ LNKCAP  = 0x%08X   MAXSPD=%d(%s)  ★MAXWIDTH=%d"
              % (lnkcap, lnkcap & 0xF, spd_name(lnkcap & 0xF), (lnkcap >> 4) & 0x3F))
        if lnksta is not None:
            P("    LNKSTA  = 0x%08X   SPD=%d(%s)     WIDTH=%d   (当前实际训练结果)"
              % (lnksta, lnksta & 0xF, spd_name(lnksta & 0xF), (lnksta >> 4) & 0x3F))
        if lnkcap2 is not None:
            P("  ★ LNKCAP2 = 0x%08X   SLSV=%d(支持的速率向量)" % (lnkcap2, (lnkcap2 >> 1) & 0x7F))
        if lnkctl2 is not None:
            P("    LNKCTL2 = 0x%08X   TLS=%d(目标速率)" % (lnkctl2, lnkctl2 & 0xF))
        if devcap is not None:
            P("    DEVCAP  = 0x%08X" % devcap)
        if devctl is not None:
            P("    DEVCTL  = 0x%08X" % devctl)
        if devsta is not None:
            P("    DEVSTA  = 0x%08X" % devsta)
    else:
        P("  X 找不到 PCIe 能力结构")
    P()

    # ---- 上游根端口
    P("【3】上游根端口（作为参考）")
    root = s.find_root_port(b, d, f)
    if root:
        rb, rd, rf = root
        P("  根端口 %02X:%02X.%d" % (rb, rd, rf))
        for c in range(0x40, 0x100, 4):
            v = s.prd(rb, rd, rf, c)
            if v is not None and (v & 0xFF) == 0x10:
                rlc = s.prd(rb, rd, rf, c + 0x0C)
                rls = s.prd(rb, rd, rf, c + 0x12)
                if rlc is not None:
                    P("    LNKCAP=0x%08X  MAXSPD=%d  MAXWIDTH=%d"
                      % (rlc, rlc & 0xF, (rlc >> 4) & 0x3F))
                if rls is not None:
                    P("    LNKSTA=0x%08X  SPD=%d   WIDTH=%d"
                      % (rls, rls & 0xF, (rls >> 4) & 0x3F))
                break
    else:
        P("  找不到根端口")
    P()

    # ---- BAR0 关键寄存器
    P("【4】BAR0 关键寄存器")
    key = [
        (0x088000, "PCIID (VENDOR/DEVICE)"),
        (0x08802C, "SUBSYSTEM_ID"),
        (0x088040, "SUBSYSTEM_ID_WR"),
        (0x08805C, "OVERRIDE (DEVID_WR/RD)"),
        (0x088084, "LNKCAP 影子"),
        (0x08808A, "LNKSTA 影子"),
        (0x0880A4, "★ LNKCAP2 影子（声明向量）"),
        (0x0880A8, "LNKCTL2 影子"),
        (0x08841C, "PRIV_MISC_1 (GEN2_DIS)"),
        (0x088610, "VSEC_HIER (bit12=sil)"),
        (0x08872C, "LTSSM"),
        (0x08C040, "LINK_CONFIG_0"),
        (0x08C080, "PPCI_2.WIDTH"),
        (0x08C1C0, "PL_LINK_RATE"),
        (0x02241C, "★ PUNITS.PCI (PCIE_VERSION/SPEED)"),
    ]
    for off, name in key:
        v = s.rd(off)
        tag = sentinel(v)
        P("  0x%06X %-32s = %s %s"
          % (off, name, ("0x%08X" % v) if v is not None else "None", ("<< " + tag) if tag else ""))
    P()

    # ---- PUNITS 页整页（★ 差分重点）
    P("【5】★ PUNITS 页 0x022400-0x0227FF（256 dword，差分重点）")
    dump_region(s, lines, P, 0x022400, 256, "PUNITS")
    P()

    # ---- PFUSE 页整页（★ 差分重点）
    P("【6】★ PFUSE 页 0x021000-0x021FFF（1024 dword，SKU 配置最可能在这）")
    dump_region(s, lines, P, 0x021000, 1024, "PFUSE")
    P()

    # ---- PPCI_2 页
    P("【7】PPCI_2 页 0x08C000-0x08C3FF（256 dword）")
    dump_region(s, lines, P, 0x08C000, 256, "PPCI_2")
    P()

    # ---- 小结
    P("=" * 78)
    P("【8】小结（★ 对比时看这几行）")
    if cap and lnkcap is not None and lnksta is not None:
        P("  ★ MAXSPD=%d(%s)   ★ MAXWIDTH=%d"
          % (lnkcap & 0xF, spd_name(lnkcap & 0xF), (lnkcap >> 4) & 0x3F))
        P("    实际 SPD=%d(%s)      实际 WIDTH=%d"
          % (lnksta & 0xF, spd_name(lnksta & 0xF), (lnksta >> 4) & 0x3F))
    P()
    P("  设备: %04X:%04X" % (vid_did & 0xFFFF, (vid_did >> 16) & 0xFFFF) if vid_did else "  设备: ?")

    # ---- 落盘
    out = "%s_%s.txt" % (OUT_PREFIX, time.strftime("%Y%m%d_%H%M%S"))
    with open(out, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))
    print()
    print("已保存 -> %s" % os.path.abspath(out))
    print("★ 把这个文件发回来即可做差分对比。")


def dump_region(s, lines, P, base, count, name):
    """整页 dump：只列非哨兵 + 统计"""
    real = 0
    poison = 0
    nonzero = []
    for i in range(count):
        off = base + i * 4
        v = s.rd(off)
        tag = sentinel(v)
        if tag in ("POISON(0xBADF****)", "READ_FAIL"):
            poison += 1
            continue
        real += 1
        if v:
            nonzero.append((off, v))
    P("  可读真实值 %d / %d ，中毒 %d ，非零 %d" % (real, count, poison, len(nonzero)))
    P("  --- 非零点 ---")
    for off, v in nonzero:
        P("    0x%06X = 0x%08X" % (off, v))
    lines.append("###REGION %s base=0x%06X count=%d real=%d poison=%d"
                 % (name, base, count, real, poison))


if __name__ == "__main__":
    main()
