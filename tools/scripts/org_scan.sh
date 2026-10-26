#!/usr/bin/env bash
# Reads each org's Description/Objective/History from Funcom's people site (JSON), one request at a time,
# and records any web links. Resumable: orgs already in done.txt are skipped.
P="$1"; UA="AOForumArchiveSurvey/1.0 (personal research on Anarchy Online history; one request at a time)"
touch "$P/done.txt" "$P/links.tsv" "$P/errors.txt"
declare -A done; while read -r id; do done[$id]=1; done < "$P/done.txt"
while IFS=$'\t' read -r id name members faction; do
  [ -n "${done[$id]}" ] && continue
  body=$(curl -s -A "$UA" --max-time 30 "https://people.anarchy-online.com/org/stats/d/5/name/$id/basicstats.xml?data_type=json" | head -c 12000)
  if [ -z "$body" ]; then echo "$id" >> "$P/errors.txt"; sleep 10; continue; fi
  text=$(printf '%s' "$body" | grep -oE '"(DESCRIPTION|OBJECTIVE|HISTORY)": "([^"\]|\.)*"' | sed -E 's/^"[A-Z]+": "//; s/"$//' | tr '\n' ' ')
  printf '%s' "$text" | grep -oiE '(https?://|www\.)[^ <>"\]+|[a-z0-9][a-z0-9.-]*\.(com|net|org|info|de|no|dk|se|fr|co\.uk|nl|be|ch|tk|us|ca|eu|biz|cc|ws)(/[^ <>"\]*)?' | sed -E 's/[).,;:!]+$//' | sort -u | while read -r u; do printf '%s\t%s\t%s\t%s\t%s\n' "$id" "$name" "$members" "$faction" "$u" >> "$P/links.tsv"; done
  echo "$id" >> "$P/done.txt"
  sleep 1
done < "$P/queue.tsv"
echo "scan finished"
