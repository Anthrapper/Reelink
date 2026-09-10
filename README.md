# Reelink

## What it does

- **Duplicate series.** When two physical `Series` records share configured external IDs (TVDB, TMDB, IMDb by default), Reelink gives them and their seasons/episodes a shared presentation identity, so Jellyfin displays one show containing content from both folders.
- **Movie and episode versions.** Repeated movies and episodes are grouped into version groups: one library item with selectable playback versions. Splitting separates them again.
- **Visual merge preview.** Scan for duplicates, review every proposed group (titles, years, matched IDs, proposed primary version), deselect groups or individual items, then merge only your selection. Selections are revalidated server-side before anything changes.

## Install

1. Push to GitHub; the **Build plugin package** workflow produces a Jellyfin-installable ZIP (artifact on every run, attached to Releases for `v*` tags, and committed under `repo/` on `main` as a plugin repository).
2. In Jellyfin 12, add the repository: **Dashboard → Plugins → Repositories → +**, paste the manifest URL below, then install **Reelink** from the catalog:

   ```
   https://raw.githubusercontent.com/Anthrapper/Reelink/main/repo/manifest.json
   ```
3. Alternatively, install the ZIP manually (or extract it so the DLL lands in a `Reelink` folder under the plugins directory) and restart.
4. Open **Dashboard → Plugins → Reelink** to configure and run scans.
5. Scheduled tasks: **Merge Duplicate Shows**, **Merge All Movies**, **Merge All Episodes** (24h interval, also configurable to run after each library scan).

## Build

Requires the .NET 10 SDK:

```sh
dotnet build Jellyfin.Plugin.Reelink.slnx -c Release
```

The assembly is written to `Jellyfin.Plugin.Reelink/bin/Release/net10.0/Jellyfin.Plugin.Reelink.dll`.

## Matching rules

- **Series:** a pair must share at least the configured number of provider IDs; any conflicting configured ID prevents merging.
- **Movies:** an exact shared `Tmdb`, `Imdb`, or `Tvdb` ID. Collection/franchise IDs (e.g. `TmdbCollection`), custom providers, and title/year are never movie keys — sequels in a trilogy are not merged.
- **Episodes:** a shared provider ID, or the same series/season/episode position.
- Every detected group logs its exact shared matching keys, so false matches are easy to diagnose.
- Excluded library locations apply to movie/episode merging only.
- Merging is idempotent; re-running is safe.

## API

All endpoints require administrator access. Selected merges must contain at least two distinct items per group, reject an item appearing in two groups, and are revalidated against the live library before anything changes — stale selections return `409 Conflict`, malformed requests return `400`.

**Preview (read-only):**

- `GET /Reelink/PreviewSeries`
- `GET /Reelink/PreviewMovies`
- `GET /Reelink/PreviewEpisodes`

**Selected merge** (body: `{ "groups": [ { "itemIds": ["<guid>", "<guid>"] } ] }`):

- `POST /Reelink/MergeSelectedSeries`
- `POST /Reelink/MergeSelectedMovies`
- `POST /Reelink/MergeSelectedEpisodes`

**Bulk operations:**

- `POST /Reelink/MergeSeries` / `POST /Reelink/SplitSeries`
- `POST /Reelink/MergeMovies` / `POST /Reelink/SplitMovies`
- `POST /Reelink/MergeEpisodes` / `POST /Reelink/SplitEpisodes`

## Configuration

- **Provider priority** — comma-separated provider names for series matching (default: `Tvdb,Tmdb,Imdb`). The first shared ID determines the group key.
- **Required matching IDs** — minimum shared provider IDs per series pair (default: 1; raise to 2 for stricter matching).
- **Run after each library scan** — reapplies series grouping after scans.
- **Preview only** — logs detected series groups (including selected merges) without changing anything.
- **Excluded locations** — library paths skipped by movie/episode version merging.

## Diagnostics

Every operation writes structured start, scan, completion, cancellation, and failure messages to the server log. Search for `Reelink`, `ShowMergeManager`, or `VideoVersionsManager`; each operation carries a `ReelinkOperationId` that ties its messages together. Information-level logs include counts, matching keys, and changed item IDs; debug-level logs add provider identities and media paths (enable temporarily via Jellyfin's logging settings). Common log locations: `/var/log/jellyfin/` (Linux packages), `/config/log/` (containers).

## Notes

- A normal metadata refresh may regenerate series presentation keys; keep **Run after each library scan** enabled to reapply.
- Version groups use Jellyfin 12's linked-alternate-version storage: alternates point at a primary (chosen by highest default-stream resolution), and splitting restores them as independent items.
