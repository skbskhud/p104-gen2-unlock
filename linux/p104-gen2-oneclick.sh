#!/usr/bin/env bash
# ============================================================================
#  P104 / P106 PCIe Gen2 x4 解锁 —— Linux 一键脚本
#
#  用法（普通用户直接运行，会自动请求 sudo）：
#      bash p104-gen2-oneclick.sh            # 一键解锁（推荐）
#      bash p104-gen2-oneclick.sh --status   # 只读查看状态
#      bash p104-gen2-oneclick.sh --restore  # 恢复默认
#
#  原理与 Windows 版一致：
#      读 PCIe Express 能力 → 改 LINKCTL2 目标速率为 Gen2
#      → remove + rescan 让链路重新协商 → 复查 LINKSTATUS
#
#  ⚠️ 社区测试版：尚未在真实 P104 上验证。请先停掉所有 GPU 任务。
# ============================================================================
set -uo pipefail

STATE_DIR="/var/tmp/p104-gen2"
LOG_FILE="$STATE_DIR/last-run.log"
PCI_IDS=("1b87" "1bc7" "1c07" "1c09" "1b07")
MODEL_HINT="P104/P106/P102"

MODE="apply"
case "${1:-}" in
  --status)  MODE="status" ;;
  --restore) MODE="restore" ;;
  -h|--help)
    sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
    exit 0 ;;
  "") ;;
  *) echo "未知参数: $1（可用 --status / --restore / --help）" >&2; exit 1 ;;
esac

# ---------- 自提权 ----------
if [ "$(id -u)" -ne 0 ]; then
  echo "需要 root 权限，正在通过 sudo 重新运行..."
  exec sudo -- bash "$0" "$@"
fi

mkdir -p "$STATE_DIR"
: > "$LOG_FILE"
exec > >(tee -a "$LOG_FILE") 2>&1

BG=$'\033[1m'; RED=$'\033[31m'; GRN=$'\033[32m'; YLW=$'\033[33m'; RST=$'\033[0m'
info() { printf '%s\n' "$*"; }
ok()   { printf '%s✓ %s%s\n' "$GRN" "$*" "$RST"; }
warn() { printf '%s! %s%s\n' "$YLW" "$*" "$RST"; }
bad()  { printf '%s✗ %s%s\n' "$RED" "$*" "$RST"; }

command -v lspci >/dev/null 2>&1 || { bad "缺少 lspci，请先安装 pciutils"; exit 1; }
command -v setpci >/dev/null 2>&1 || { bad "缺少 setpci，请先安装 pciutils"; exit 1; }

info "${BG}== ${MODEL_HINT} Linux Gen2 解锁（社区测试版） ==${RST}"
info "时间: $(date '+%F %T')"
info ""

# ---------- 找卡 ----------
CARDS=()
for id in "${PCI_IDS[@]}"; do
  while IFS= read -r bdf; do
    [ -n "$bdf" ] && CARDS+=("$bdf")
  done < <(lspci -d "10de:${id}" 2>/dev/null | awk '{print $1}')
done

if [ "${#CARDS[@]}" -eq 0 ]; then
  bad "没有找到 ${MODEL_HINT} 设备"
  info "已枚举的 NVIDIA 设备："
  lspci -d 10de: 2>/dev/null | sed 's/^/  /'
  exit 1
fi
ok "找到 ${#CARDS[@]} 张卡: ${CARDS[*]}"
info ""

# ---------- 工具函数 ----------
express_off() {
  local bdf="$1"
  lspci -s "$bdf" -vv 2>/dev/null \
    | grep -m1 -E 'Capabilities:.*Express' \
    | sed -n 's/.*\[\([0-9a-fA-F]\{2\}\)\].*/\1/p'
}

hex2() { printf '%02x' "$(( $1 ))"; }

readreg() {
  local bdf="$1" off="$2" width="$3" a
  a=$(hex2 "$off")
  setpci -s "$bdf" "${a}.${width}" 2>/dev/null
}

writereg() {
  local bdf="$1" off="$2" val="$3" a
  a=$(hex2 "$off")
  setpci -s "$bdf" "${a}.w=${val}" 2>/dev/null
}

speed_name() {
  case "$1" in
    1) echo "2.5 GT/s (Gen1)" ;;
    2) echo "5.0 GT/s (Gen2)" ;;
    3) echo "8.0 GT/s (Gen3)" ;;
    *) echo "未知($1)" ;;
  esac
}

dump_status() {
  local bdf="$1" off hx cap cap2 ctl2 sta sp wd tgt spd tgtd
  off=$(express_off "$bdf")
  if [ -z "$off" ]; then
    bad "  $bdf: 找不到 Express capability"
    return 1
  fi
  hx=$((16#$off))
  cap=$(readreg "$bdf" $((hx + 0x0c)) l)
  cap2=$(readreg "$bdf" $((hx + 0x2c)) l)
  ctl2=$(readreg "$bdf" $((hx + 0x30)) w)
  sta=$(readreg "$bdf" $((hx + 0x12)) w)
  if [ -z "$cap" ] || [ -z "$ctl2" ]; then
    bad "  $bdf: 读取寄存器失败（cap @ 0x$off）"
    return 1
  fi
  sp=$((0x${sta:-0} & 0xF))
  wd=$(((0x${sta:-0} >> 4) & 0x3F))
  tgt=$((0x${ctl2} & 0xF))
  spd=$(speed_name "$sp")
  tgtd=$(speed_name "$tgt")
  info "  ── $bdf  (Express cap @ 0x$off)"
  info "     LINKCAP    = 0x$cap"
  info "     LINKCAP2   = 0x$cap2"
  info "     LINKCTL2   = 0x$ctl2   (目标速率=$tgt $tgtd)"
  info "     LINKSTATUS = 0x$sta   (当前=$spd，宽度=x$wd)"
  return 0
}

rescan_one() {
  local bdf="$1" dev="/sys/bus/pci/devices/$bdf"
  if [ -d "$dev" ]; then
    info "     重新枚举 $bdf ..."
    echo 1 > "$dev/remove" 2>/dev/null || true
    sleep 2
  fi
  echo 1 > /sys/bus/pci/rescan 2>/dev/null || true
  sleep 3
}

state_file() { echo "$STATE_DIR/$(echo "$1" | tr ':/' '__').state"; }

# ---------- 模式 ----------
case "$MODE" in
status)
  info "${BG}[状态]${RST}"
  for c in "${CARDS[@]}"; do dump_status "$c"; done
  exit 0
  ;;
restore)
  info "${BG}[恢复]${RST}"
  for c in "${CARDS[@]}"; do
    sf=$(state_file "$c")
    off=$(express_off "$c")
    [ -n "$off" ] || continue
    hx=$((16#$off))
    if [ -f "$sf" ]; then
      orig=$(cat "$sf")
      info "  $c: 写回 LINKCTL2=0x$orig"
      writereg "$c" $((hx + 0x30)) "$orig"
      rescan_one "$c"
      dump_status "$c"
    else
      warn "  $c: 没有备份文件（$sf），跳过"
    fi
  done
  exit 0
  ;;
esac

# apply
info "${BG}[当前状态]${RST}"
for c in "${CARDS[@]}"; do dump_status "$c"; done
info ""

for c in "${CARDS[@]}"; do
  info "${BG}── 处理 $c ──${RST}"
  off=$(express_off "$c")
  if [ -z "$off" ]; then bad "  找不到 Express capability，跳过"; continue; fi
  hx=$((16#$off))

  ctl2=$(readreg "$c" $((hx + 0x30)) w)
  if [ -z "$ctl2" ]; then bad "  读取 LINKCTL2 失败，跳过"; continue; fi

  sf=$(state_file "$c")
  printf '%s' "$ctl2" > "$sf"
  info "  备份 LINKCTL2=0x$ctl2 → $sf"

  target=$(( (0x$ctl2 & ~0xF) | 2 ))
  target_hex=$(printf '%04x' "$target")
  info "  写入 LINKCTL2=0x$target_hex（目标 Gen2）"
  if ! writereg "$c" $((hx + 0x30)) "$target_hex"; then
    bad "  写入失败"
    continue
  fi

  sleep 1
  rescan_one "$c"

  off=$(express_off "$c")
  if [ -z "$off" ]; then
    bad "  重新枚举后找不到设备（可能需要重启）"
    continue
  fi
  hx=$((16#$off))
  sta=$(readreg "$c" $((hx + 0x12)) w)
  sp=$((0x${sta:-0} & 0xF))
  wd=$(((0x${sta:-0} >> 4) & 0x3F))
  if [ "$sp" -eq 2 ]; then
    ok "  成功：链路 = $(speed_name 2), 宽度 x$wd"
  else
    warn "  未生效：当前 $(speed_name "$sp"), 宽度 x$wd"
    warn "  可尝试重启后再跑一次；或运行 --restore 回退"
  fi
  info ""
done

info "${BG}[最终状态]${RST}"
for c in "${CARDS[@]}"; do dump_status "$c"; done
info ""
info "日志: $LOG_FILE"
info "${BG}完成。${RST}如需回退: sudo bash $0 --restore"
