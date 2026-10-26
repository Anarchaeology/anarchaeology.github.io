#!/bin/bash
# Lists every thread of Funcom's AO forum archive that the Wayback Machine saved, one CDX index page at a time.
# Polite: one request at a time, 6 s apart; on 429/5xx/timeouts waits 60 s, doubling up to 15 min.
# Keeps only "thread_id <tab> slug <tab> page_number <tab> first_capture" per saved thread page (post permalinks dropped).
F="$(dirname "$0")"
UA="AOForumArchiveSurvey/1.0 (personal research on Anarchy Online history; one request at a time, 5-7 s apart)"
BASE="https://web.archive.org/cdx/search/cdx?url=forums-archive.anarchy-online.com/showthread.php&matchType=prefix&fl=timestamp,original&collapse=urlkey"
PAGES=61
for ((p = 0; p < PAGES; p++)); do
  out="$F/threads_p$p.tsv"
  [ -s "$out" ] && continue
  wait=60
  while :; do
    code=$(curl -s -A "$UA" --max-time 300 -o "$F/raw.tmp" -w '%{http_code}' "$BASE&page=$p")
    if [ "$code" = 200 ]; then break; fi
    echo "page $p: HTTP $code, waiting ${wait}s" >> "$F/progress.log"
    sleep $wait
    wait=$((wait * 2)); [ $wait -gt 900 ] && wait=900
  done
  grep -v '[?&]p=' "$F/raw.tmp" \
    | sed -nE 's#^([0-9]{14}) https?://forums-archive\.anarchy-online\.com(:80)?/showthread\.php\?(t=)?([0-9]+)(-[^&]*)?(.*)$#\4\t\5\t\6\t\1#p' \
    | awk -F'\t' 'BEGIN{OFS="\t"} { pg = 1; if (match($3, /page=[0-9]+/)) pg = substr($3, RSTART + 5, RLENGTH - 5)
                                      if (match($2, /\/page[0-9]+$/)) { pg = substr($2, RSTART + 5); $2 = substr($2, 1, RSTART - 1) }
                                      sub(/^-/, "", $2); print $1, $2, pg, $4 }' > "$out.part"
  mv "$out.part" "$out"
  echo "page $p: $(wc -l < "$F/raw.tmp") lines -> $(wc -l < "$out") thread pages" >> "$F/progress.log"
  sleep 6
done
rm -f "$F/raw.tmp"
# Summary: distinct threads, and saved pages per thread (highest page number seen).
cat "$F"/threads_p*.tsv | awk -F'\t' 'BEGIN{OFS="\t"} { if (!($1 in mx) || $3 + 0 > mx[$1]) mx[$1] = $3 + 0; if ($2 != "") slug[$1] = $2 } END { for (id in mx) print id, mx[id], slug[id] }' \
  | sort -n > "$F/threads_all.tsv"
echo "DONE: $(wc -l < "$F/threads_all.tsv") threads" >> "$F/progress.log"
