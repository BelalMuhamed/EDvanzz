#!/usr/bin/env bash
# Guard: every "now" in business code is UTC, and every business "today" is the TENANT's.
#
# These are not style rules. `DateTime.Now`, `DateTimeOffset.Now`, `TimeZoneInfo.Local` and
# `.ToLocalTime()` read the SERVER's clock, which is UTC on the Azure Linux plan and the
# developer's own zone locally — so a bug written this way is invisible in development and
# wrong in production. `DateTime.UtcNow.Date` is a subtler one: it is still YESTERDAY between
# midnight and 2-3 AM Cairo, which silently flipped an Expired chip, dropped a session off the
# center schedule, and let a genuinely past assignment date pass validation, for the first
# hours of every day. Taking `.Date` off a variable that was assigned `DateTime.UtcNow`
# (`var now = DateTime.UtcNow; ... now.Date`) is the SAME bug wearing a local name, so it is
# caught too.
#
# Correct instead:
#   a moment in time         -> DateTime.UtcNow
#   the teacher's today/now  -> ITimeZoneService.GetTeacherLocalDate / GetTeacherLocalNow
#   a repo that needs today  -> take it as a parameter from the calling service
#
# See TIMEZONE_STANDARD.md, including its list of deliberate exceptions.
set -uo pipefail

PROJECTS=(Edvanz.Domain Edvanz.Application Edvanz.Infrastructure Edvanz.API)

# ── ALLOWLIST ────────────────────────────────────────────────────────────────────────────
# EXACT `path:line` entries, one per deliberate hit — NOT whole files. A whole-file entry
# would exempt every future line in that file too, which is how five entire files (including
# two hot payment/attendance services) were silently un-guarded until 2026-09-09.
#
# When one of these lines MOVES, the guard fails on its new line number: that is intended
# (someone edited around a known landmine). Fix it by updating the number here, not by
# widening the entry to the file. The run also prints a note about entries that no longer
# point at a hit, so stale lines get cleaned up instead of accumulating.
ALLOW=(
  # A coarse UTC window padded a day either side on purpose; the per-teacher worker re-gates on
  # the teacher's local date, so over-selecting only costs a fast no-op.
  'Edvanz.Application/Services/AttendanceAutoAbsentService.cs:118'
  # The subscription module compares EndDate.Date with UtcNow.Date throughout and is internally
  # consistent; its dispatcher runs at 09:00 Africa/Cairo where the two dates always agree.
  # Changing one site alone would make daysRemaining and the reminder disagree by a day.
  'Edvanz.Application/Services/SubscriptionReminderService.cs:68'
  # Seed data only; never runs against a real tenant.
  'Edvanz.Infrastructure/Persistence/DbInitializer.Relationships.cs:156'
  # Template project file left by `dotnet new`.
  'Edvanz.API/Controllers/WeatherForecastController.cs:26'
)

# Records which allowlist entries were actually used, so unused ones can be reported.
USED=""

is_allowed() {
  # $1 = "path:line"
  for entry in "${ALLOW[@]}"; do
    if [ "$1" = "$entry" ]; then
      USED="$USED $entry"
      return 0
    fi
  done
  return 1
}

# Pattern -> what to do instead.
PATTERNS=(
  'DateTime\.Now|use DateTime.UtcNow, or ITimeZoneService for a teacher-local now'
  'DateTime\.Today|use ITimeZoneService.GetTeacherLocalDate(teacherId)'
  'DateTimeOffset\.Now|use DateTimeOffset.UtcNow, or ITimeZoneService for a teacher-local now'
  'TimeZoneInfo\.Local|the server zone is not the tenant zone — use ITimeZoneService'
  '\.ToLocalTime\(\)|use ITimeZoneService.ConvertUtcToLocal'
  'DateTime\.UtcNow\.Date|use ITimeZoneService.GetTeacherLocalDate(teacherId) — UtcNow.Date is still yesterday between midnight and 2-3 AM Cairo'
)

# Several files in this repo have SPACES in their names (e.g. the "VCM Phase 1 schema
# changes" migrations, and Edvanz.Domain/Interfaces/"IUnitOfWork .cs"), so file lists are
# always passed NUL-delimited. A whitespace-split list silently skips those files.
list_sources() {
  find "${PROJECTS[@]}" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print0 2>/dev/null
}

if [ -z "$(list_sources | tr '\0' '\n' | head -n 1)" ]; then
  echo "Timezone guard: no source files found — run me from the repo root."
  exit 1
fi

failed=0
report() {
  # $1 = "path:line:code", $2 = advice
  if [ "$failed" -eq 0 ]; then
    echo "Timezone guard failed (TIMEZONE_STANDARD.md):"
    echo
  fi
  failed=1
  echo "  $1"
  echo "      -> $2"
}

# ── PASS 1: literal patterns ─────────────────────────────────────────────────────────────
for spec in "${PATTERNS[@]}"; do
  pattern="${spec%%|*}"
  advice="${spec#*|}"

  while IFS= read -r hit; do
    [ -z "$hit" ] && continue
    rest="${hit#*:}"
    loc="${hit%%:*}:${rest%%:*}"
    is_allowed "$loc" && continue
    report "$hit" "$advice"
  done < <(list_sources | xargs -0 grep -nE "$pattern" /dev/null 2>/dev/null \
             | grep -vE '^[^:]+:[0-9]+:[[:space:]]*(//|\*)')
done

# ── PASS 2: a UTC "now" that later has .Date taken off it ────────────────────────────────
# `var now = DateTime.UtcNow;` ... `now.Date` is exactly `DateTime.UtcNow.Date` with an extra
# step, and pass 1 cannot see it. Scoped per file and deliberately conservative: only names
# assigned STRAIGHT from DateTime.UtcNow (no .Date, no .AddDays) count, and only a `.Date` on
# that same name is flagged. Comment lines are skipped.
while IFS= read -r hit; do
  [ -z "$hit" ] && continue
  rest="${hit#*:}"
  loc="${hit%%:*}:${rest%%:*}"
  is_allowed "$loc" && continue
  report "$hit" 'this is DateTime.UtcNow.Date by another name — use ITimeZoneService.GetTeacherLocalDate(teacherId)'
done < <(list_sources | xargs -0 awk '
  FNR == 1 { delete utc }
  {
    trimmed = $0
    sub(/^[ \t]*/, "", trimmed)
    if (trimmed ~ /^(\/\/|\*)/) next
    if (match($0, /(var|(System\.)?DateTime)[ \t]+[A-Za-z_][A-Za-z0-9_]*[ \t]*=[ \t]*(System\.)?DateTime\.UtcNow[ \t]*;/)) {
      name = substr($0, RSTART, RLENGTH)
      sub(/^(var|(System\.)?DateTime)[ \t]+/, "", name)
      sub(/[ \t]*=.*$/, "", name)
      utc[name] = 1
    }
    for (n in utc) {
      if ($0 ~ "(^|[^A-Za-z0-9_])" n "\\.Date([^A-Za-z0-9_]|$)") {
        print FILENAME ":" FNR ":" $0
        break
      }
    }
  }' 2>/dev/null)

# ── Stale-allowlist note (advisory only; never fails the build) ──────────────────────────
stale=""
for entry in "${ALLOW[@]}"; do
  case " $USED " in
    *" $entry "*) ;;
    *) stale="$stale
  $entry" ;;
  esac
done
if [ -n "$stale" ]; then
  echo "Timezone guard note: allowlist entries that no longer match a hit (the line moved, or"
  echo "the code was fixed). Update the line number or delete the entry:$stale"
  echo
fi

if [ "$failed" -ne 0 ]; then
  echo
  echo "If a hit is genuinely intentional, add its EXACT path:line to ALLOW in this script WITH"
  echo "the reason, and record the exception in TIMEZONE_STANDARD.md. Do not allowlist a whole file."
  exit 1
fi

echo "Timezone guard passed: no server-clock or UTC-day derivations in business code."
