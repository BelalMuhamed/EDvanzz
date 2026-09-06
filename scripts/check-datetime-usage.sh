#!/usr/bin/env bash
# Guard: every "now" in business code is UTC, and every business "today" is the TENANT's.
#
# These are not style rules. `DateTime.Now` and `.ToLocalTime()` read the SERVER's clock, which
# is UTC on the Azure Linux plan and the developer's own zone locally — so a bug written this
# way is invisible in development and wrong in production. `DateTime.UtcNow.Date` is a subtler
# one: it is still YESTERDAY between midnight and 2-3 AM Cairo, which silently flipped an
# Expired chip, dropped a session off the center schedule, and let a genuinely past assignment
# date pass validation, for the first hours of every day.
#
# Correct instead:
#   a moment in time         -> DateTime.UtcNow
#   the teacher's today/now  -> ITimeZoneService.GetTeacherLocalDate / GetTeacherLocalNow
#   a repo that needs today  -> take it as a parameter from the calling service
#
# See TIMEZONE_STANDARD.md, including its list of deliberate exceptions.
set -uo pipefail

PROJECTS=(Edvanz.Domain Edvanz.Application Edvanz.Infrastructure Edvanz.API)

# path:line entries that are deliberate. Keep the reason with the entry.
ALLOW=(
  # A coarse UTC window padded a day either side on purpose; the per-teacher worker re-gates on
  # the teacher's local date, so over-selecting only costs a fast no-op.
  'Edvanz.Application/Services/AttendanceAutoAbsentService.cs'
  # The subscription module compares EndDate.Date with UtcNow.Date throughout and is internally
  # consistent; its dispatcher runs at 09:00 Africa/Cairo where the two dates always agree.
  # Changing one site alone would make daysRemaining and the reminder disagree by a day.
  'Edvanz.Application/Services/SubscriptionReminderService.cs'
  'Edvanz.Domain/Helpers/SubscriptionStatusCalculator.cs'
  # Seed data only; never runs against a real tenant.
  'Edvanz.Infrastructure/Persistence/DbInitializer.Relationships.cs'
  # Template project file left by `dotnet new`.
  'Edvanz.API/Controllers/WeatherForecastController.cs'
)

is_allowed() {
  local file="$1"
  for entry in "${ALLOW[@]}"; do
    [[ "$file" == "$entry" ]] && return 0
  done
  return 1
}

# Pattern -> what to do instead.
declare -a PATTERNS=(
  'DateTime\.Now|use DateTime.UtcNow, or ITimeZoneService for a teacher-local now'
  'DateTime\.Today|use ITimeZoneService.GetTeacherLocalDate(teacherId)'
  '\.ToLocalTime\(\)|use ITimeZoneService.ConvertUtcToLocal'
  'DateTime\.UtcNow\.Date|use ITimeZoneService.GetTeacherLocalDate(teacherId) — UtcNow.Date is still yesterday between midnight and 2-3 AM Cairo'
)

failed=0
for spec in "${PATTERNS[@]}"; do
  pattern="${spec%%|*}"
  advice="${spec#*|}"

  while IFS= read -r hit; do
    [[ -z "$hit" ]] && continue
    file="${hit%%:*}"
    is_allowed "$file" && continue
    if [[ $failed -eq 0 ]]; then
      echo "Timezone guard failed (TIMEZONE_STANDARD.md):"
      echo
    fi
    failed=1
    echo "  $hit"
    echo "      -> $advice"
  done < <(grep -rnE "$pattern" --include='*.cs' "${PROJECTS[@]}" 2>/dev/null \
             | grep -vE '^[^:]+:[0-9]+:\s*(//|///|\*)' \
             | grep -v '/obj/' | grep -v '/bin/')
done

if [[ $failed -ne 0 ]]; then
  echo
  echo "If a hit is genuinely intentional, add its file to ALLOW in this script WITH the reason,"
  echo "and record the exception in TIMEZONE_STANDARD.md."
  exit 1
fi

echo "Timezone guard passed: no server-clock or UTC-day derivations in business code."
