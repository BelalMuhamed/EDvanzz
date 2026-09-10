#!/usr/bin/env bash
# ============================================================================
# run-portal-probe.sh — READ-ONLY production diagnostic for the parent portal.
#
#   1. opens a temporary Azure SQL firewall rule for THIS machine's IP
#   2. reads the connection string straight from the App Service setting into a
#      shell variable (never printed, never written to disk)
#   3. runs tools/PortalProbe (SELECTs only — nothing is written)
#   4. closes the firewall rule again, even if the probe fails
#
# Requires: az (already logged in as admin@edvanz.io) and dotnet.
# Usage:    bash tools/PortalProbe/run-portal-probe.sh
#
# To look up a different parent/student than the reported one:
#   PROBE_NAME='فتحى' PROBE_PHONE='1272666' bash tools/PortalProbe/run-portal-probe.sh
# ============================================================================
set -uo pipefail

APP="app-edvanz-api-prod"
RG="rg-edvanz-prod-weu"
SQL_SERVER="sql-edvanz-prod-weu"
RULE="tmp-portal-probe-$(date +%s)"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

cleanup() {
  echo
  echo "→ closing firewall rule $RULE"
  az sql server firewall-rule delete -g "$RG" -s "$SQL_SERVER" -n "$RULE" >/dev/null 2>&1 \
    && echo "  closed." || echo "  (rule not present)"
}
trap cleanup EXIT

MYIP="$(curl -fsS https://api.ipify.org)" || { echo "could not determine public IP"; exit 1; }
echo "→ this machine: $MYIP"

echo "→ opening firewall rule $RULE"
az sql server firewall-rule create -g "$RG" -s "$SQL_SERVER" -n "$RULE" \
  --start-ip-address "$MYIP" --end-ip-address "$MYIP" >/dev/null || {
    echo "could not create the firewall rule"; exit 1; }

echo "→ reading the connection string from App Service (not printed)"
EDVANZ_CS="$(az webapp config appsettings list -n "$APP" -g "$RG" \
  --query "[?name=='ConnectionStrings__con'].value" -o tsv)"
if [ -z "${EDVANZ_CS:-}" ]; then
  echo "ConnectionStrings__con is empty or unreadable"; exit 1
fi
export EDVANZ_CS

echo "→ running the read-only probe"
echo
dotnet run --project "$HERE/PortalProbe.csproj" --configuration Release -v quiet
STATUS=$?

unset EDVANZ_CS
exit $STATUS
