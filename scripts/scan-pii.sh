#!/bin/sh
# Scans the repository for personal data taken from a live member database before committing.
# Usage: scripts/scan-pii.sh [path/to/member.db]
# Prints only file names, never the matched values. Exits 1 if anything matches.
set -eu
cd "$(dirname "$0")/.."
db=${1:-"$HOME/Library/Application Support/Activout.MemberData/member.db"}
patterns=$(mktemp)
trap 'rm -f "$patterns"' EXIT

sqlite3 -readonly "$db" <<'SQL' | awk 'length($0) >= 6' | sort -u > "$patterns"
SELECT value FROM database_info WHERE key IN ('member', 'developer_application');
SELECT DISTINCT owner FROM changelog_events UNION SELECT DISTINCT actor FROM changelog_events
  UNION SELECT DISTINCT resource_id FROM changelog_events UNION SELECT DISTINCT activity_id FROM changelog_events;
SELECT json_extract(json, '$."First Name"') || ' ' || json_extract(json, '$."Last Name"') FROM snapshot_records WHERE domain IN ('CONNECTIONS', 'PROFILE', 'RECOMMENDATIONS');
SELECT json_extract(json, '$.URL') FROM snapshot_records WHERE domain = 'CONNECTIONS';
SELECT json_extract(json, '$."Email Address"') FROM snapshot_records WHERE domain IN ('CONNECTIONS', 'EMAIL_ADDRESSES');
SELECT json_extract(json, '$.Number') FROM snapshot_records WHERE domain IN ('PHONE_NUMBERS', 'WHATSAPP_NUMBERS');
SELECT json_extract(json, '$."CONVERSATION ID"') FROM snapshot_records WHERE domain = 'INBOX';
SELECT json_extract(json, '$."SENDER PROFILE URL"') FROM snapshot_records WHERE domain = 'INBOX';
SELECT json_extract(json, '$."Headline"') FROM snapshot_records WHERE domain = 'PROFILE';
SQL

echo "scan-pii: $(wc -l < "$patterns" | tr -d ' ') distinct values from the live database"
if git ls-files -co --exclude-standard -z | xargs -0 grep -l -F -f "$patterns" -- 2>/dev/null; then
  echo "scan-pii: FOUND personal data in the files above" >&2
  exit 1
fi
echo "scan-pii: OK"
