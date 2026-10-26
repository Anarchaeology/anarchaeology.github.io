#!/usr/bin/env bash
# Reads the original upload date (X-Archive-Orig-Last-Modified) of later-saved pictures' first saved copies, to find
# 2001-2003 uploads that were only saved later. One HEAD request at a time, 6 s apart; on 429/5xx/timeouts waits
# 60 s, doubling up to 15 min. Resumable: addresses already in dates.tsv are skipped.
# Per site: first a sample of its 8 earliest-saved pictures; then the rest of each folder where anything uploaded
# by 2003 turned up (early uploads cluster in folders; whole sites would be ~11 hours, mostly later files).
# Writes dates.tsv: first saved, address, bytes, source, http status, original Last-Modified (raw).
D="$1"; UA="AOForumArchiveSurvey/1.0 (personal research on Anarchy Online history; one request at a time, 5-7 s apart)"
touch "$D/dates.tsv"
host() { local u="${1#*://}"; u="${u%%/*}"; u="${u%%:*}"; echo "${u#www.}" | tr 'A-Z' 'a-z'; }
folder() { local u="${1#*://}"; u="${u/:80\//\/}"; u="${u#www.}"; echo "${u%/*}/" | tr 'A-Z' 'a-z'; }
year() { echo "$1" | awk '{print $4}'; }
check() {   # ts url len source
  local wait=60 h code lm
  while :; do
    h=$(curl -s -I -A "$UA" --max-time 60 "https://web.archive.org/web/${1}id_/${2}" | tr -d '\r')
    code=$(printf '%s\n' "$h" | head -1 | cut -d' ' -f2)
    case "$code" in 429|5*|"") echo "$(date '+%F %T') HTTP ${code:-timeout} for $2; waiting ${wait}s" >> "$D/progress.log"; sleep $wait; wait=$((wait * 2)); [ $wait -gt 900 ] && wait=900; continue;; esac
    break
  done
  lm=$(printf '%s\n' "$h" | grep -i '^x-archive-orig-last-modified:' | head -1 | cut -d' ' -f2-)
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "$code" "$lm" >> "$D/dates.tsv"
  sleep 6
}
declare -A done sampled old
while IFS=$'\t' read -r ts url len source code lm; do
  done[$url]=1; h=$(host "$url"); sampled[$h]=$(( ${sampled[$h]:-0} + 1 ))
  y=$(year "$lm"); [ -n "$y" ] && [ "$y" -le 2003 ] && old[$(folder "$url")]=1
done < "$D/dates.tsv"
# Pass 1: up to 8 earliest-saved pictures per site, sites interleaved.
sort -t$'\t' -k1,1 "$D/candidates.tsv" | awk -F'\t' '{u=$2; sub(/^https?:\/\//,"",u); split(u, p, "[/:]"); h=tolower(p[1]); sub(/^www\./,"",h); n[h]++; if (n[h] <= 8) print n[h] "\t" $0}' | sort -t$'\t' -k1,1n -k2,2 | cut -f2- > "$D/pass1.tsv"
echo "$(date '+%F %T') pass 1: sampling $(wc -l < "$D/pass1.tsv") pictures from $(cut -f2 "$D/pass1.tsv" | sed -E 's#^https?://##; s#[/:].*##; s#^www\.##' | sort -u | wc -l) sites" >> "$D/progress.log"
while IFS=$'\t' read -r ts url len source; do
  [ -n "${done[$url]}" ] && continue
  h=$(host "$url"); [ "${sampled[$h]:-0}" -ge 8 ] && continue
  check "$ts" "$url" "$len" "$source"; done[$url]=1; sampled[$h]=$(( ${sampled[$h]:-0} + 1 ))
  y=$(tail -1 "$D/dates.tsv" | cut -f6 | awk '{print $4}'); [ -n "$y" ] && [ "$y" -le 2003 ] && old[$(folder "$url")]=1
done < "$D/pass1.tsv"
# Pass 2: the rest of the folders where a 2001-2003 upload turned up.
echo "$(date '+%F %T') pass 2: ${#old[@]} folders with early uploads" >> "$D/progress.log"
while IFS=$'\t' read -r ts url len source; do
  [ -n "${done[$url]}" ] && continue
  [ -z "${old[$(folder "$url")]}" ] && continue
  check "$ts" "$url" "$len" "$source"; done[$url]=1
done < <(sort -t$'\t' -k1,1 "$D/candidates.tsv")
echo "$(date '+%F %T') finished" >> "$D/progress.log"
