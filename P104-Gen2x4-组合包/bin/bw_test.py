#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
bw_test.py —— P104-100 PCIe 带宽实测（不依赖任何 NVIDIA 工具）

★ 为什么需要它：
    nvidia-smi / GPU-Z 读的是【驱动上报值】，在这张无显示输出的矿卡上不可靠。
    PCIe 链路的【真实速率】只能靠带宽实测来判定。

判据（x4 宽度，256MB 传输，取多次中位数）：
    Gen1 x4  →  H2D ≈ 0.78 GB/s
    Gen2 x4  →  H2D ≈ 1.55 GB/s      <- 本工具的目标
    Gen3 x4  →  H2D ≈ 3.10 GB/s

用法：
    python bw_test.py
"""
import sys
import time

try:
    import torch
except ImportError:
    print("")
    print("  ✗ 没装 torch，无法实测带宽。")
    print("")
    print("  替代方案（不需要 Python）：")
    print("    打开 GPU-Z → 点 'Bus Interface' 右边的【?】按钮")
    print("    它会做一次真实的 PCIe 带宽测试，并显示实测结果。")
    print("")
    print("  参考值（x4 宽度）：")
    print("    Gen1 x4 ≈ 0.78 GB/s")
    print("    Gen2 x4 ≈ 1.55 GB/s")
    print("    Gen3 x4 ≈ 3.10 GB/s")
    sys.exit(1)

if not torch.cuda.is_available():
    print("  ✗ CUDA 不可用 —— 请确认 NVIDIA 驱动正常、显卡在总线上。")
    sys.exit(1)

dev = torch.cuda.get_device_name(0)
N = 256 * 1024 * 1024          # 256 MB
REPEAT = 7

print("=" * 66)
print("P104-100 PCIe 带宽实测")
print("=" * 66)
print("  设备   : %s" % dev)
print("  数据量 : %d MB × %d 次（取中位数）" % (N // (1024 * 1024), REPEAT))
print("")

cpu = torch.empty(N // 4, dtype=torch.float32, pin_memory=True)
gpu = torch.empty(N // 4, dtype=torch.float32, device="cuda")

h2d, d2h = [], []
for i in range(REPEAT):
    torch.cuda.synchronize()
    t0 = time.perf_counter()
    gpu.copy_(cpu, non_blocking=False)
    torch.cuda.synchronize()
    t1 = time.perf_counter()
    h2d.append(N / (t1 - t0) / 1e9)

    t0 = time.perf_counter()
    cpu.copy_(gpu, non_blocking=False)
    torch.cuda.synchronize()
    t1 = time.perf_counter()
    d2h.append(N / (t1 - t0) / 1e9)

h2d.sort()
d2h.sort()
m_h2d = h2d[len(h2d) // 2]
m_d2h = d2h[len(d2h) // 2]

print("  H2D (主机→显卡) : %.4f GB/s" % m_h2d)
print("  D2H (显卡→主机) : %.4f GB/s" % m_d2h)
print("")
print("  ------------------------------------------------------------")
if m_h2d >= 2.4:
    verdict, note = "Gen3 x4（或更快）", "★ 远超 Gen2，请核实"
elif m_h2d >= 1.25:
    verdict, note = "Gen2 x4", "★ 目标达成"
elif m_h2d >= 0.60:
    verdict, note = "Gen1 x4", "未解锁 —— 检查按住进程是否在运行"
elif m_h2d >= 0.30:
    verdict, note = "Gen2 x2 或 Gen1 x2", "宽度只有 2 条 lane"
else:
    verdict, note = "Gen1 x1 或更低", "宽度只有 1 条 lane"
print("  ★ 判定：%s   （%s）" % (verdict, note))
print("  ------------------------------------------------------------")
print("")
print("  参考值（x4）：Gen1 = 0.78 | Gen2 = 1.55 | Gen3 = 3.10 GB/s")
print("=" * 66)
