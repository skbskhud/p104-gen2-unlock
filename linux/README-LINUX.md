# Linux 版：P104 PCIe Gen2 x4 解锁（社区测试版）

> ⚠️ **尚未在 Linux 实机验证**：本脚本由 Windows 版移植，原理相同，但 Linux 路径还没在真实 P104 上跑过。
> 请在能物理接触、能重启的机器上试，不要在远程生产机上直接跑。

## 原理（与 Windows 版一致）

```
1) 读 PCIe Express capability 里的 LINKCAP / LINKCAP2 / LINKCTL2 / LINKSTATUS
2) 把 LINKCTL2 的目标链路速率字段（bits[3:0]）改成 2 = 5.0 GT/s（Gen2）
3) remove + rescan（或 unbind/bind 驱动）让链路重新协商
4) 复查 LINKSTATUS 是否变成 5.0 GT/s
```

Windows 版需要绕驱动签名（加载临时内核驱动）；Linux 直接 `setpci` 即可。

## 用法（一键）

```bash
chmod +x p104-gen2-oneclick.sh

sudo ./p104-gen2-oneclick.sh            # 一键解锁
sudo ./p104-gen2-oneclick.sh --status   # 只读查看状态
sudo ./p104-gen2-oneclick.sh --restore  # 恢复默认
```

普通用户直接运行也可以：脚本会自动 `sudo` 重新执行。

## 输出示例（期望结果）

```
  ── 01:00.0  (Express cap @ 0xa8)
     LINKCAP    = 0x00453d02
     LINKCAP2   = 0x00000006
     LINKCTL2   = 0x0002   (目标速率=2 5.0 GT/s (Gen2))
     LINKSTATUS = 0x0042   (当前=5.0 GT/s (Gen2)，宽度=x4)
✓ 成功：链路 = 5.0 GT/s (Gen2), 宽度 x4
```

## 风险

```
· 写 PCIe 寄存器 + 重新枚举时，如果显卡正在跑计算任务，可能掉设备；
  掉设备后通常重启即可恢复，不会损坏硬件。
· 建议先停掉所有 GPU 任务（nvidia-smi 确认没有进程）。
· 脚本会把原 LINKCTL2 备份到 /var/tmp/p104-gen2/*.state，可 --restore 恢复。
· 与 Windows 版一样：只改链路速率，不刷 VBIOS、不写熔丝。
```

## 待确认

```
· 重训练是否一定需要 remove+rescan；unbind/bind 是否足够
· NVIDIA 闭源驱动是否会在驱动重置后把目标速率写回 Gen1（需要实测）
· 不同主板/芯片组表现可能不同
```

## 反馈

请附上：

```
1) lspci -s <BDF> -vv 的 PCIe 段（LnkCap/LnkCap2/LnkCtl2/LnkSta）
2) 脚本输出（--status 与解锁后）
3) 发行版、内核版本、NVIDIA 驱动版本、主板型号
4) 是否成功（LnkSta 是否变成 5.0 GT/s）
```

Windows 版：见仓库根目录 `README.md` 与 Releases。
