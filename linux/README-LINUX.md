# Linux 版：P104 / P106 PCIe Gen2 x4 解锁（社区测试版）

> ⚠️ **未经实机验证**：本脚本由 Windows 版移植而来，原理相同，但 Linux 侧尚未在真实 P104 上测试。
> 请在能物理接触、能重启的机器上试，不要在远程生产机上直接跑。

## 原理（与 Windows 版一致）

```
1) 读 PCIe Express capability 里的 LINKCAP / LINKCAP2 / LINKCTL2 / LINKSTATUS
2) 把 LINKCTL2 的目标链路速率字段（bits[3:0]）改成 2 = 5.0 GT/s（Gen2）
3) 需要时 remove + rescan（或 unbind/bind 驱动），让链路重新协商
4) 复查 LINKSTATUS 是否变成 5.0 GT/s
```

Windows 版还需要绕驱动签名（加载临时内核驱动）；Linux 直接 `setpci` 即可，更简单。

## 环境

```
· Linux x86-64
· root 权限
· pciutils（lspci、setpci）
· 支持的卡：P104-100 / P104-101 / P106-100 / P106-090 / P102-100
```

## 用法

```bash
chmod +x p104-gen2-linux.sh

# 只读查看当前链路状态
sudo ./p104-gen2-linux.sh

# 应用 Gen2
sudo ./p104-gen2-linux.sh --apply

# 应用并重新枚举（部分平台需要）
sudo ./p104-gen2-linux.sh --apply --reset

# 恢复原值
sudo ./p104-gen2-linux.sh --restore
```

输出示例（期望结果）：

```
  LINKCAP2    : 0x00000006  (bits[3:0]=0x6)
  LINKCTL2    : 0x00000002  (目标速度=2 5.0 GT/s (Gen2))
  LINKSTATUS  : 0x00000042  (当前=5.0 GT/s (Gen2), 宽度=x4)
✅ 成功：链路已升到 5.0 GT/s (Gen2)
```

## 风险

```
· 写 PCIe 寄存器 + 重新枚举时，如果显卡正在跑计算任务，可能掉设备；
  掉设备后通常重启即可恢复，不会损坏硬件。
· 建议先停掉所有 GPU 任务（nvidia-smi 确认没有进程）。
· 脚本会备份原 LINKCTL2 到 /tmp/p104-gen2-linux.state，可 --restore 恢复。
· 与 Windows 版一样：只改链路速率，不刷 VBIOS、不写熔丝。
```

## 已知/待确认

```
· 未验证的点：重训练是否一定需要 remove+rescan；unbind/bind 是否足够
· NVIDIA 闭源驱动是否会在驱动重置后把目标速率写回 Gen1（需要实测）
· 不同主板/芯片组表现可能不同
```

## 反馈

请附上：

```
1) lspci -s <BDF> -vv  （PCIe 段：LnkCap/LnkCap2/LnkCtl2/LnkSta）
2) 脚本输出（--apply 前后）
3) 发行版、内核版本、驱动版本、主板型号
4) 是否成功（LnkSta 是否变成 5.0 GT/s）
```

Windows 版：见仓库根目录 `README.md` 与 Releases。
