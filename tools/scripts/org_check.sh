#!/usr/bin/env bash
# Checks whether each link found in org descriptions still answers. One request per link, 1 s apart;
# already-checked links are skipped. Writes live.tsv: link, status (live/dead/parked), http code, final address, page title.
P="$1"; UA="Mozilla/5.0 (compatible; AOForumArchiveSurvey/1.0; checking whether old Anarchy Online community sites are still online)"
page="$P/chk.html"
touch "$P/live.tsv"
declare -A seen; while IFS=$'\t' read -r u rest; do seen[$u]=1; done < "$P/live.tsv"
cut -f5 "$P/links_clean.tsv" | sort -u | while read -r raw; do
  [ -n "${seen[$raw]}" ] && continue
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
  printf '%s\t%s\t%s\t%s\t%s\n' "$raw" "$status" "$code" "$final" "$title" >> "$P/live.tsv"
  sleep 1
done
echo "check finished"
