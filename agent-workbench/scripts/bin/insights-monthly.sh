#!/usr/bin/env bash
# Runs /insights when the last report is older than MAX_AGE_DAYS.
#
# Called daily from cron rather than monthly on purpose: WSL is not always up at
# a fixed monthly moment, and cron here has no anacron-style catch-up. A daily
# check that exits immediately costs nothing and never misses a month by more
# than a day of uptime.

set -u

CLAUDE_BIN="$HOME/.local/bin/claude"
DATA_DIR="$HOME/.claude/usage-data"
REPORT="$DATA_DIR/report.html"
LOG="$DATA_DIR/insights-cron.log"
LOCK="$DATA_DIR/.insights-cron.lock"
MAX_AGE_DAYS=28
TIMEOUT_SECONDS=1800

log() { printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >>"$LOG"; }

mkdir -p "$DATA_DIR"

# Keep the log from growing without bound.
if [ -f "$LOG" ] && [ "$(wc -c <"$LOG")" -gt 262144 ]; then
    tail -n 200 "$LOG" >"$LOG.tmp" && mv "$LOG.tmp" "$LOG"
fi

if [ ! -x "$CLAUDE_BIN" ]; then
    log "SKIP: $CLAUDE_BIN not executable"
    exit 0
fi

if [ -f "$REPORT" ] && [ -z "$(find "$REPORT" -mtime "+$MAX_AGE_DAYS" -print -quit)" ]; then
    exit 0
fi

# mkdir is atomic, so a still-running previous invocation is not doubled.
if ! mkdir "$LOCK" 2>/dev/null; then
    log "SKIP: another run holds the lock"
    exit 0
fi
trap 'rmdir "$LOCK" 2>/dev/null' EXIT

log "START: report older than $MAX_AGE_DAYS days"
cd "$HOME" || exit 0
if timeout "$TIMEOUT_SECONDS" "$CLAUDE_BIN" -p "/insights" >>"$LOG" 2>&1; then
    log "DONE: $(ls -l "$REPORT" 2>/dev/null | awk '{print $6, $7, $8}')"
else
    log "FAILED with exit $?"
fi
