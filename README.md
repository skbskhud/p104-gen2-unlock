# P104-100 PCIe Link Research

**English** | [中文](README.zh-CN.md)

> A complete, measurement-driven investigation into the PCIe link speed, link width, and
> video codec engines of one NVIDIA **P104-100** (GP104 / Pascal mining card).
>
> **The only practical result: Gen1 x4 → Gen2 x4, roughly doubling unidirectional bandwidth
> (0.78 → 1.55 GB/s).** The other three directions (Gen3 / x16 width / codecs) are documented
> with explicit conclusions *and evidence*, including **why they are not possible**.

This project is a **measurement and reverse-engineering record** — not a cracking tutorial,
and not a tool for bypassing device restrictions. Every conclusion carries an evidence-strength
label, and every dead end is preserved as-is (see [`docs/03-走过的弯路.md`](docs/03-走过的弯路.md)).

---

## Conclusions at a glance

| Direction | Conclusion | Strength |
|---|---|---|
| **Gen2 x4 (5.0 GT/s)** | ✅ **Achievable and stable** — bandwidth roughly doubled | 【Measured】Reproducible, tool packaged |
| **Gen3 x4 (8.0 GT/s)** | ❌ **Not achievable.** The endpoint itself refuses: setting Gen3 as the target rate is clamped back to Gen2 by hardware immediately | 【Measured】All 4527 attempts clamped back |
| **Link width x4 → x16** | ❌ **Cannot be changed**, and **soldering capacitors is not recommended**. The endpoint declares x4 as its maximum width; link width = min(both ends' declarations) | 【Measured】+【Strong support】 |
| **NVENC / NVDEC codecs** | ❓ **Unresolved.** Engine hardware exists, driver code paths exist, but they are blocked at the firmware/fuse layer | 【Strong support】No host-side entry point found |

**Gen3 is not "we haven't found the method yet" — the device itself refuses.**
This is the strongest form of negative conclusion this project can offer. It rests on five
mutually independent lines of evidence — see §4 of [`docs/01-技术报告.md`](docs/01-技术报告.md).

---

## Download

**→ Tool packages are on the [Releases](../../releases) page.**

| File | Contents |
|---|---|
| `P104-Gen2x4-v1.5.zip` | PCIe Gen2 x4 unlock tool (source, docs, status-check scripts included) |

> Note: this repository only covers **P104-100 PCIe link** research and tooling.
> It does **not** include any VBIOS reader/flasher — this project explicitly recommends
> against flashing VBIOS (see the technical report for reasoning).

---

## Repository layout

The repository has **two layers**; only the first is distributed:

| Layer | Contents | Shipped in releases |
|---|---|---|
| **Public** | `README.md` + `docs/` + `tools/` + tool release packages | ✅ Yes |
| **Internal** | `notes/_archive/` (raw experiment logs), `_internal/` (workspace navigation), `_scratch/` (dev scripts) | ❌ No |

`notes/_archive/` holds dozens of **raw experiment reports and run logs** (with their original
wording). They are **kept as-is for evidence traceability and are not distributed**
(excluded via `.gitignore`). The public docs (`docs/`) keep only conclusions and decisive
evidence; where the two conflict, **`docs/` wins**.

---

## What's in here

```
README.md                  ← the file you are reading
README.zh-CN.md            ← Chinese version
docs/                      ★ Public documentation (each has a .txt plain-text twin)
  01-技术报告.md           Core: conclusions for all three directions + decisive evidence + methodology
  02-工具说明.md           Tool usage, FAQ, troubleshooting
  03-走过的弯路.md         Retracted conclusions and overturned judgments (★ avoid repeating our mistakes)
  04-硬件事实表.md         Register bitfields / fuse map / measured values / dangerous operations
tools/                     Helper scripts (bandwidth test, codec probing, etc.)
(Tool release package)     The Gen2 unlock tool, distributed as a standalone release
notes/_archive/            [not distributed] raw process records
_internal/                 [not distributed] workspace navigation
```

**About the tool package:** the Gen2 unlock tool ships as a **standalone release package** —
**directory and archive names may change between versions**; refer to the Releases page and
what you actually get after extracting. Command names (`install.cmd` / `status.cmd` /
`check-bw.cmd` / `uninstall.cmd`) and usage are documented in
[`docs/02-工具说明.md`](docs/02-工具说明.md).

Register bitfield definitions and driver-source references live in `_scratch/refs/` in the
development workspace (envytools rnndb + nouveau sources, **not distributed**;
`04-硬件事实表.md` reproduces every bitfield this project actually used).

---

## Quick start (three steps to verify whether Gen2 is achievable)

> Applies only to **NVIDIA P104-100 (`10DE:1B87`) + Windows 10/11 x64 + administrator rights**.
> The tool needs two kernel drivers to read/write GPU registers — **antivirus will flag them
> (expected behaviour)**; see §3 of [`docs/02-工具说明.md`](docs/02-工具说明.md) for the reason
> and how to handle it. **Please read that first.**

```bat
:: 1) Install (includes one full device reset + ~30 s wait)
install.cmd

:: 2) Check current status (no admin needed)
status.cmd

:: 3) Verify bandwidth: Gen1 x4 ≈ 0.78 GB/s, Gen2 x4 ≈ 1.55 GB/s
check-bw.cmd
```

`status.cmd` prints **all the evidence you need to judge**: `LNKSTA` (current link speed),
`LNKCAP2` (the endpoint's declared capabilities), and a measured bandwidth figure.
**Only bandwidth and `LNKSTA` count as evidence** — `LNKCAP`/`LNKCAP2` are *declared
capabilities*, not the current speed.

Roll back:

```bat
uninstall.cmd
```

---

## Key numbers

| State | H2D (host→VRAM) | D2H (VRAM→host) |
|---|---|---|
| x1 @ Gen1 | 0.197 GB/s | 0.209 GB/s |
| x1 @ Gen2 | 0.390 GB/s | 0.420 GB/s |
| **x4 @ Gen1 (factory)** | **0.778 GB/s** | **0.835 GB/s** |
| **x4 @ Gen2 (this project)** | **1.55 GB/s** | **1.69 GB/s** |
| x4 @ Gen3 (theoretical ceiling, not reached) | ≈ 3.1 GB/s | ≈ 3.1 GB/s |

Register fingerprints (same card, same slot):

```
               Gen1 state     Gen2 state
LNKSTA         0x00001041     0x00001042     ← ★ current link speed — the only reliable speed criterion
LNKCAP2        0x00000002     0x00000006     ← which speeds the endpoint declares it supports
LNKCAP         0x00463C41     0x00454C42     ← max speed / width declaration
LINK_CONFIG_0  0x20585800     0x20545800
PL_LINK_RATE   0x00110036     0x00120036
```

---

## Test environment (the platform this project measured on)

```
GPU        NVIDIA P104-100, 10DE:1B87, subsys 10DE:1237, at 01:00.0
Upstream   00:01.0 (8086:9A01), CPU-attached PEG, declares Gen4 x16
OS         Windows 11 x64
CPU        Intel i9-14900KS
Board      Intel HM570 modded board, BIOS THM570111
```

Register access is done through two publicly signed kernel drivers
(`WinRing0x64.sys` + `ThrottleStop.sys`). **Conclusions may differ on another platform or
another card** — this project is only responsible for this one card on this one platform; the
coverage boundary of every conclusion is stated in its own document.

---

## Evidence-strength convention

All documents in this repository use one five-level scale, **never mixed**:

| Label | Meaning |
|---|---|
| **【Measured】** | Backed by repeatable hardware readings or bandwidth measurements; raw values given |
| **【Strong support】** | Multiple independent lines of evidence point the same way, but no single decisive experiment |
| **【Inference】** | Reasoning chain exists but lacks direct evidence; explicitly labelled as inference |
| **【Unverified】** | Not done, or done but the result is not interpretable |
| **【Retracted】** | Once treated as a conclusion, later overturned by experiment or better analysis (see 03) |

---

## Safety & disclaimer

- Every write in this project targets **GPU PCIe/BAR0 registers** and is fully reversible
  (`uninstall.cmd`).
- **No VBIOS / EEPROM / Flash writing** (this project explicitly advises against flashing for
  this purpose — see §6 of the technical report).
- **No hardware modifications recommended** (soldering capacitors, etc.) — see §6 of the
  technical report.
- The kernel drivers this tool relies on are classified in security circles as **BYOVD**
  (bring your own vulnerable driver). Antivirus / anti-cheat will block them — this is
  **expected behaviour**, not a false positive worth complaining about. Evaluate your own
  environment before use.
- This project is only for researching hardware you own. Do not use it on someone else's device.

---

## Methodology takeaways (arguably more valuable than the conclusions)

1. **Only "current state" can serve as a criterion.** Using capability-declaration registers as
   a speed criterion once produced three false "success" reports.
2. **A negative conclusion must be paired with a control write.** Before claiming "cannot
   write", you must first prove that "writing some *other* register at the same moment does
   land".
3. **Exhaustive beats sampling — but exhaustive must record and control the baseline state.**
4. **"Same write, different result" is a hidden-variable alarm.** If an identical write gives
   different outcomes at different times, there is an unidentified state variable.
5. **State your coverage boundary up front.** This project's register scan only covers the
   portion the device exposes to the host (about 23.5% of dwords in the first 1 MB of BAR0 are
   readable; under the strict "whole page readable" criterion only 1.9%).
   **"Not found" and "swept" are two different statements.**

More retracted judgments and pitfalls: [`docs/03-走过的弯路.md`](docs/03-走过的弯路.md).

---

## How this project was made

**AI assistance was used extensively in the research and writing of this project.**

This is stated here because readers deserve to know how these materials came about.

### Division of work

| Stage | Led by |
|---|---|
| Experiment design, hardware operations, data judgement, final conclusions | **Human-led**, verified point by point |
| Code, documentation, register/disassembly analysis, external research | **AI-assisted**, then human-reviewed |

### What this means

- **Every conclusion in these documents is backed by reproducible experimental data**, with its
  evidence strength labelled individually (【Measured】/【Strong support】/【Inference】/
  【Unverified】/【Retracted】).
- **Judge whether the content is trustworthy by the data and the experimental steps**, not by
  who the author is.
- The **wrong calls and retracted conclusions** explicitly recorded here (see
  [`docs/03-走过的弯路.md`](docs/03-走过的弯路.md)) are what actually happened during the
  research; they are kept so that others can avoid the same dead ends.

### What this does not mean

- It does not mean the conclusions are unverified. On the contrary, this project holds
  **negative** conclusions to a higher standard: any "cannot write / cannot do" claim must be
  accompanied by a **control write that lands**, otherwise it can only be written as
  "indeterminate".
- It does not replace your own judgement. If you reproduce different results, **your
  measurements win**.

---

*If this material is useful to you, its value comes from the experiments that can be reproduced
— not from who wrote it or with what tools.*
