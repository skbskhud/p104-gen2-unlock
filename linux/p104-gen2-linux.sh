#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# p104-gen2-linux.sh — P104-100 / P106-100 (GP104) PCIe Gen1 x4 -> Gen2 x4
#                     Linux 版（与 Windows 版同一原理：写 LNKCTL2 目标速率）
#
# ⚠️ 本脚本【未经 Linux 实机验证】，属于社区测试版。
#    先在能物理接触、能重启的机器上试；不要在远程生产机上直接跑。
#
# 原理：
#   1) 找到 GPU 的 PCIe Extended Capability（Express）偏移
#   2) 读 LINKCAP / LINKCAP2 / LINKCTL2 / LINKSTATUS
#   3) 把 LINKCTL2 的目标链路速率字段（bits[3:0]）改成 2 = 5.0 GT/s (Gen2)
#   4) 需要时重新绑定驱动 / remove+rescan，让链路重新协商
#   5) 复查 LINKSTATUS 是否变成 5.0 GT/s
#
# 用法：
#   sudo ./p104-gen2-linux.sh                 # 自动找卡，显示状态（只读）
#   sudo ./p104-gen2-linux.sh --apply         # 应用 Gen2
#   sudo ./p104-gen2-linux.sh --apply --reset # 应用并触发设备重新枚举
#   sudo ./p104-gen2-linux.sh --restore       # 恢复原目标速率
#   sudo ./p104-gen2-linux.sh --bdf 01:00.0   # 指定设备
#
# 依赖：pciutils（lspci、setpci）、root
# ---------------------------------------------------------------------------
set -euo pipefail

TARGET_SPEED="${TARGET_SPEED:-2}"   # 1=2.5GT/s, 2=5.0GT/s, 3=8.0GT/s
APPLY=0
RESET=0
RESTORE=0
BDF=""
STATE_FILE="${STATE_FILE:-/tmp/p104-gen2-linux.state}"

PCI_IDS=(
  "1b87:P104-100"
  "1c07:P106-100"
  "1c09:P106-090"
  "1b07:P102-100"
  "1bc7:P104-101"
)

log()  { printf '%s\n' "$*"; }
die()  { printf '错误: %s\n' "$*" >&2; exit 1; }

usage() {
  sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'
  exit 0
}

while [ $# -gt 0 ]; do
  case "$1" in
    --apply)   APPLY=1 ;;
    --reset)   RESET=1 ;;
    --restore) RESTORE=1 ;;
    --bdf)     shift; BDF="${1:-}"; [ -n "$BDF" ] || die "--bdf 需要一个参数，如 01:00.0" ;;
    --speed)   shift; TARGET_SPEED="${1:-}"; [ -n "$TARGET_SPEED" ] || die "--speed 需要 1/2/3" ;;
    -h|--help) usage ;;
    *) die "未知参数: $1（用 --help 看用法）" ;;
  esac
  shift
done

[ "$(id -u)" -eq 0 ] || die "需要 root（读写 PCI 配置空间）"
command -v lspci >/dev/null || die "缺少 lspci（安装 pciutils）"
command -v setpci >/dev/null || die "缺少 setpci（安装 pciutils）"

# --- 找卡 -------------------------------------------------------------------
find_card() {
  local entry dev name
  for entry in "${PCI_IDS[@]}"; do
    dev="${entry%%:*}"
    name="${entry#*:}"
    if lspci -d "10de:${dev}" 2>/dev/null | grep -q .; then
      BDF="$(lspci -d "10de:${dev}" | head -n1 | awk '{print $1}')"
      log "找到 $name @ $BDF"
      return 0
    fi
  done
  return 1
}

[ -n "$BDF" ] || find_card || die "没找到 P104/P106/P102 设备（可用 --bdf 指定）"

# --- 找 Express cap 偏移 -----------------------------------------------------
express_off() {
  local off
  off="$(lspci -s "$BDF" -vv 2>/dev/null \
        | grep -m1 -E 'Capabilities:.*Express' \
        | sed -n 's/.*\[\([0-9a-fA-F]\{2\}\)\].*/\1/p')"
  [ -n "$off" ] || die "找不到该设备的 PCIe Express capability"
  printf '%s' "$off"
}

OFF="$(express_off)"
HEXOFF=$((16#$OFF))

addr() { printf '%02x' $((HEXOFF + $1)); }

rd_l() { setpci -s "$BDF" "$(addr 0x$2)".l; }
rd_w() { setpci -s "$BDF" "$(addr 0x$2)".w; }
wr_w() { setpci -s "$BDF" "$(addr 0x$2)".w="$3"; }

speed_name() {
  case "$1" in
    1) echo "2.5 GT/s (Gen1)" ;;
    2) echo "5.0 GT/s (Gen2)" ;;
    3) echo "8.0 GT/s (Gen3)" ;;
    4) echo "16 GT/s (Gen4)" ;;
    *) echo "未知($1)" ;;
  esac
}

show_state() {
  local cap cap2 ctl2 sta speed width
  cap="$(rd_l 0x0c)"; cap2="$(rd_l 0x2c)"
  ctl2="$(rd_w 0x30)"; sta="$(rd_w 0x12)"
  speed=$((0x$sta & 0xF))
  width=$(((0x$sta >> 4) & 0x3F))
  log "  设备        : $BDF"
  log "  LINKCAP     : 0x$cap   (支持速度位=0x$((0x$cap & 0xF)))"
  log "  LINKCAP2    : 0x$cap2  (bits[3:0]=0x$((0x$cap2 & 0xF)))"
  log "  LINKCTL2    : 0x$ctl2  (目标速度=$((0x$ctl2 & 0xF)) $(speed_name $((0x$ctl2 & 0xF))))"
  log "  LINKSTATUS  : 0x$sta  (当前=$(speed_name "$speed"), 宽度=x$width)"
}

# --- 重新枚举 ---------------------------------------------------------------
rescan_device() {
  local devpath="/sys/bus/pci/devices/$BDF"
  [ -d "$devpath" ] || die "找不到 $devpath"
  log "  → remove + rescan ..."
  echo 1 > "$devpath/remove" 2>/dev/null || true
  sleep 2
  echo 1 > /sys/bus/pci/rescan 2>/dev/null || true
  sleep 3
}

rebind_driver() {
  local drv
  drv="$(basename "$(readlink -f "/sys/bus/pci/devices/$BDF/driver" 2>/dev/null)" 2>/dev/null || true)"
  if [ -n "$drv" ] && [ "$drv" != "." ] && [ -d "/sys/bus/pci/drivers/$drv" ]; then
    log "  → 重新绑定驱动 $drv ..."
    echo "$BDF" > "/sys/bus/pci/drivers/$drv/unbind" 2>/dev/null || true
    sleep 1
    echo "$BDF" > "/sys/bus/pci/drivers/$drv/bind" 2>/dev/null || true
    sleep 2
  fi
}

# --- 主流程 -----------------------------------------------------------------
log "== P104/P106 Linux Gen2 解锁（社区测试版） =="
log ""
log "[当前状态]"
show_state

CTL2="$(rd_w 0x30)"
ORIG_TARGET=$((0x$CTL2 & 0xF))

if [ "$APPLY" -eq 0 ] && [ "$RESTORE" -eq 0 ]; then
  log ""
  log "只读模式。要应用 Gen2，请运行: sudo $0 --apply [--reset]"
  exit 0
fi

if [ "$RESTORE" -eq 1 ]; then
  if [ -f "$STATE_FILE" ]; then
    ORIG_CTL2="$(cat "$STATE_FILE")"
    log ""
    log "[恢复] 把 LINKCTL2 写回 0x$ORIG_CTL2"
    wr_w 0x30 "$ORIG_CTL2"
    [ "$RESET" -eq 1 ] && rescan_device
    log "[恢复后]"
    show_state
    exit 0
  else
    die "没有找到备份文件 $STATE_FILE（无法恢复）"
  fi
fi

[ "$TARGET_SPEED" -ge 1 ] && [ "$TARGET_SPEED" -le 3 ] || die "--speed 只能是 1/2/3"

log ""
log "[备份] 当前 LINKCTL2 = 0x$CTL2"
printf '%s' "$CTL2" > "$STATE_FILE"

NEW=$(( (0x$CTL2 & ~0xF) | TARGET_SPEED ))
NEWHEX="$(printf '%04x' "$NEW")"
log "[写入] LINKCTL2 = 0x$NEWHEX（目标速度=$TARGET_SPEED）"
wr_w 0x30 "$NEWHEX"

sleep 1

if [ "$RESET" -eq 1 ]; then
  log ""
  log "[重新枚举]"
  rescan_device
fi

log ""
log "[结果]"
show_state

STA="$(rd_w 0x12)"
SPEED=$((0x$STA & 0xF))
log ""
if [ "$SPEED" -eq "$TARGET_SPEED" ]; then
  log "✅ 成功：链路已升到 $(speed_name "$TARGET_SPEED")"
else
  log "❌ 未生效：当前仍是 $(speed_name "$SPEED")"
  log "   可尝试：sudo $0 --apply --reset"
  log "   若仍失败，说明该卡/平台不支持；用 --restore 恢复。"
fi
