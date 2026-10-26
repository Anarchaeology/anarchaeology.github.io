#!/usr/bin/env bash
# Rechecks links that did not answer on 21 Oct (the PHP outage day), the same way check.sh did:
# one request per link, 1 s apart. Writes result.tsv: link, status (live/dead/parked), http code, final address, title.
P="$(dirname "$0")"; UA="Mozilla/5.0 (compatible; AOForumArchiveSurvey/1.0; checking whether old Anarchy Online community sites are still online)"
page="$P/chk.html"
: > "$P/result.tsv"
while read -r raw; do
  [ -z "$raw" ] && continue
  rm -f "$page"
  u="$raw"
  shopt -s nocasematch
  if [[ "$u" =~ ^(https?)://(.*)$ ]]; then u="${BASH_REMATCH[1],,}://${BASH_REMATCH[2]}"; else u="http://$u"; fi
  shopt -u nocasematch
  out=$(curl -sL -A "$UA" --max-time 20 --max-redirs 6 -o "$page" -w '%{http_code}\t%{url_effective}' "$u" 2>/dev/null)
  code=${out%%$'\t'*}; final=${out#*$'\t'}
  title=""
  [ -f "$page" ] && title=$(tr -d '\r\n' < "$page" | grep -oiE '<title[^>]*>[^<]{0,150}' | head -1 | sed -E 's/<title[^>]*>//I' | tr -s ' \t' ' ')
  status=dead
  if [[ "$code" =~ ^2 ]]; then
    status=live
    if grep -qiE 'domain (is )?for sale|buy this domain|this domain may be for sale|parked (free|domain)|sedoparking|hugedomains|dan\.com|afternic|godaddy\.com/park|domain has expired|is for sale!|parkingcrew|bodis\.com|nameshift' "$page" 2>/dev/null \
       || [[ "$title" =~ [Ff]or\ [Ss]ale|[Pp]arked|[Dd]omain\ [Nn]ame ]] || [[ "$final" =~ nameshift|sedo|dan\.com|afternic|hugedomains|/lander|ww[0-9]+\. ]]; then
      status=parked
    fi
  fi
  printf '%s\t%s\t%s\t%s\t%s\n' "$raw" "$status" "$code" "$final" "$title" >> "$P/result.tsv"
  sleep 1
done < "$P/links.txt"
echo "recheck finished"
