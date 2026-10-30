> **Latest v3.3 (Gen2 x4 + x16 declaration + 20 ms recovery + width retrain, Windows one-click)**: download from this repo's Releases; source/methodology/deep docs -> https://github.com/skbskhud/p104-unlock-v3

# P104-100 PCIe Gen2 x4 Unlock

**English** | [中文](README.zh-CN.md)

> A small Windows tool that raises the PCIe link of an NVIDIA **P104-100** (GP104 mining card)
> from **Gen1 x4** to **Gen2 x4**, roughly doubling the measured unidirectional bandwidth.
>
> Measured on the same card and slot: **0.78 → 1.55 GB/s** (host → VRAM),
> **0.84 → 1.69 GB/s** (VRAM → host).

---

## What it does

```
Before:  PCIe Gen1 x4   ≈ 0.78 GB/s  (factory state)
After :  PCIe Gen2 x4   ≈ 1.55 GB/s  (this tool)
```

The tool retrains the endpoint to Gen2 and keeps the x4 link. The change is **reversible at any
time** with `uninstall.cmd`. No VBIOS/EEPROM is written.

---

## Measured results

| Link state | Host → VRAM | VRAM → Host |
|---|---|---|
| Gen1 x1 | 0.197 GB/s | 0.209 GB/s |
| Gen2 x1 | 0.390 GB/s | 0.420 GB/s |
| **Gen1 x4 (factory)** | **0.778 GB/s** | **0.835 GB/s** |
| **Gen2 x4 (this tool)** | **1.55 GB/s** | **1.69 GB/s** |

Register fingerprints (same card, same slot):

```
               Gen1 state     Gen2 state
LNKSTA         0x00001041     0x00001042   ← current link speed
LNKCAP2        0x00000002     0x00000006   ← speeds the endpoint declares
LNKCAP         0x00463C41     0x00454C42   ← max speed / width declaration
LINK_CONFIG_0  0x20585800     0x20545800
PL_LINK_RATE   0x00110036     0x00120036
```

---

## Requirements

```
GPU     NVIDIA P104-100 (10DE:1B87)
OS      Windows 10 / 11 x64
Rights  Administrator
```

Tested platform: P104-100 at `01:00.0`, upstream `00:01.0` (declares Gen4 x16),
Intel i9-14900KS, Windows 11 x64.

Results may differ on another board, slot or card revision — measure before and after.

---

## Quick start

```bat
:: 1) Install (includes one device reset and a short wait)
install.cmd

:: 2) Check current link state and bandwidth (no admin needed)
status.cmd
check-bw.cmd

:: 3) Roll back to factory state
uninstall.cmd
```

`status.cmd` prints `LNKSTA` (current speed), `LNKCAP2` (declared speeds) and a measured
bandwidth figure. **Judge by `LNKSTA` and the bandwidth number**, not by capability declarations.

Expected output after a successful run:

```
LNKSTA      0x00001042
Bandwidth   ~1.5 GB/s  (host -> VRAM)
```

---

## Notes

- The tool needs two kernel drivers to access PCIe configuration registers.
  **Antivirus and anti-cheat software will flag them — this is expected.**
  Add an exclusion or pause protection while using the tool, then restore it.
- Everything the tool writes is temporary register state. A reboot also returns the link to
  factory behaviour, so `uninstall.cmd` is convenient rather than mandatory.
- Do not run it while the card is driving a display you need — the device reset in step 1
  blanks the screen for a moment.
- Only for hardware you own.

---

## Version history

| Version | Notes |
|---|---|
| v1.6 | `uninstall.cmd` ends processes gracefully instead of force-killing them (a force-killed process holding a kernel-driver handle can leave the driver in a bad state) |
| v1.5 | Bandwidth check script, status script, packaging |
| v1.4 and earlier | Register write experiments |

---

## License / scope

Research tooling for your own hardware. Provided as-is, no warranty.
If you break something while using it, that is your responsibility.

---

*If this tool saves you time, the value is in the measurements behind it: always verify with
`status.cmd` and `check-bw.cmd` on your own card.*
