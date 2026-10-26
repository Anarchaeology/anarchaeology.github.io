# Tools

How the Anarchaeology site is built and how new pictures get into it. The pictures themselves live in a
**collection folder** outside this repository (`ao_wayback_photos`, with its `manifest.tsv`); the site in the
repository root is built from it. Back that folder up separately: this repository only holds the copies the
site shows.

Needs the .NET 10 SDK (the `.cs` files are single-file apps: `dotnet run Tool.cs -- ...`) and Node.js for the
scripts. Each tool's header comment explains its options in full.

## Rules the collection follows

- Every picture is a copy the Internet Archive's Wayback Machine saved, and links back to it.
- Only pictures dated 2003 or earlier. A picture's date is the earliest believable one: a date stored in the file,
  a date in its address, the original server's upload date (kept in saved copies as
  `X-Archive-Orig-Last-Modified`, even in copies saved years later), the day a forum post first linked it, or the
  day the Wayback Machine first saved it.
- Full-size pictures only; no thumbnails, real-life photos or gore.
- Be polite to the Wayback Machine: one request at a time, 5-7 seconds apart, backing off on errors.
- New finds go into a separate review folder first, are marked by hand, checked for duplicates, then merged.
  Nothing is ever deleted; turned-down pictures move to `_rejected\` and are recorded in `rejected.tsv`.

## Adding pictures

1. **Find what was saved.** `CheckWayback.cs PREFIXES.txt OUT` asks the Wayback Machine which pictures it saved
   under each address prefix (any year). Turn the results into a plan: one tab-separated row per picture with
   `tier, source, timestamp, original URL, mimetype, length, normalized URL, forum date, forum thread`.
2. **Download into a review folder.** `FetchWaybackPlan.cs PLAN.tsv REVIEWFOLDER` downloads each picture, dates
   it, and writes a contact sheet (`index.html`). Open it, mark what to drop, export the list, then
   `FetchWaybackPlan.cs PLAN.tsv REVIEWFOLDER --apply-rejects LIST.txt`.
3. **Check for duplicates against the collection.** Make a folder holding junctions `main` (the collection) and
   `new` (the review folder) and a combined `manifest.tsv` whose file paths are prefixed `main\` and `new\`, then
   `FindDuplicates.cs THATFOLDER`. It writes `duplicates.html` to check by eye and `duplicates.tsv`.
   For exact copies keep the collection's copy (its picture ID, and so any shared link, stays the same) and give it
   the earlier date with `--set-dates`; for bigger or better copies use `--apply-duplicates`.
4. **Merge.** Copy the review folder's kept files into the collection under the same paths, append their manifest
   rows to the collection's `manifest.tsv` (keep a backup), then `FetchWaybackPlan.cs PLAN.tsv COLLECTION --index-only`.

## Building and publishing the site

From this `tools` folder (the page template is `gallery-template`):

```
dotnet run BuildGallery.cs -- "PATH\TO\ao_wayback_photos" .. --title "Anarchaeology" --repo "https://github.com/Anarchaeology/anarchaeology.github.io" --site-url "https://anarchaeology.github.io/"
```

It copies new pictures into `photos/`, makes `thumbs/`, writes `data/photos.json`, `index.html` and the other
page files, and removes files for pictures no longer in the collection. It also reports site file names that look
like ads (ad blockers hide those); give such pictures another name in the collection's `site_names.tsv`
(collection path, tab, name on the site).

Preview locally with `node scripts/serve-mount.js .. 8768` and open http://localhost:8768 (opening `index.html`
straight from disk doesn't work: the browser won't let the page load its picture list).

Commit and push from the repository root. The repository is set to commit as "Anarchaeology"
(`git config user.name/user.email` in this repository only). GitHub Pages republishes within a minute or two;
visitors' browsers may keep the old picture list for up to 10 minutes.

## Other tools

| Tool | What it does |
|---|---|
| `HarvestForum.cs` | Reads saved thread pages of Funcom's forum archive from the Wayback Machine and lists the links in posts with their dates; keeps every page (compressed) |
| `ReadLiveForum.cs` | Reads pages saved from the live forum in 2001-2004 (vBulletin 2 and 3 layouts) |
| `CrawlJolForum.cs`, `ExtractJolLinks.cs` | Read JeuxOnLine's forum archive for picture and site links |
| `FetchAnarchyLore.cs` | Downloads anarchylore.com's saved full-size screenshots |
| `DateMiaJong.cs`, `MiaLevels.cs` | Date pictures from a player's numbered screenshot log and character level history |
| `CrawlSiteImages.cs` | Polite crawler (respects robots.txt) listing a live site's pictures |
| `BuildOrgSitesPage.cs` | Builds the page listing org websites from Funcom's org directory and whether they still answer |
| `MakeLogo.cs` | Makes `logo.png` and `favicon.png` from a picture |
| `ConvertWebp.cs` | Converts WebP downloads to PNG |

`scripts/` holds the helpers used along the way: forum thread listing (`list_threads.sh`, `make_pages.js`),
matching linked pictures to saved copies (`make_prefixes.js`, `match.js`, `replaced.js`), finding sites
mentioned in posts (`sites.js`, `site_prefixes.js`, `site_report.js`, `ao_sites.js`), finding later-saved
pictures uploaded by 2003 (`later_candidates.js`, `upload_dates.sh`), searching posts for names
(`find_names.js`), reading the text in pictures with Windows' built-in OCR (`ocr_all.ps1`, `ocr_find.js`),
checking org sites (`org_scan.sh`, `org_check.sh`, `recheck_links.sh`, `apply_recheck.js`), and the local
preview server (`serve-mount.js`).
