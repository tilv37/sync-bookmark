#!/bin/sh
# 备份 bmsync 数据目录。
#
# 装 crontab：  crontab -e   然后加一行
#   17 3 * * *  /opt/bmsync/deploy/backup.sh >> /var/log/bmsync-backup.log 2>&1
#
# 为什么重要：data/ 里装着唯一的权威状态。虽然 history/ 里有 30 份快照，
# 但那些是"同步过程中的中间态"，不是"我今天备份过的状态"。真出问题时
# 你想要的是一份可以直接恢复的整体备份。

set -eu

cd "$(dirname "$0")"

STAMP=$(date -u +%Y%m%dT%H%M%SZ)
KEEP=14
OUT="backup/data-$STAMP.tar.gz"

mkdir -p backup

# --exclude 排掉可能残留的临时文件：它只在校验通过后才被 rename 生效，
# 备份它没有意义。
tar -czf "$OUT" \
    --exclude='data/state.json.tmp' \
    data/

# 只保留最近 KEEP 份
ls -1t backup/data-*.tar.gz 2>/dev/null | tail -n "+$((KEEP + 1))" | xargs -r rm --

# 提示人工异地备份。这个脚本不做上传 —— 你把备份放哪里取决于你信谁。
echo "$(date -u +%FT%TZ) 备份完成: $OUT ($(du -h "$OUT" | cut -f1))"
echo "  保留最近 $KEEP 份。当前份数: $(ls -1 backup/data-*.tar.gz 2>/dev/null | wc -l)"
