# Movies: plan

**Status (2026-11-11):** steps 1 and 2 are built. That covers the backend (facade, validator, API; renamed `GetLibraries`, `GetVideo`, `VideoFacts`) and the page plus mock data, with tests. The two proposed rules below are implemented as written; change them if the owner decides otherwise. Step 3 (Testflix) is partly done: browse and preview. Step 4 (Bazarr movies) is next for the Bazarr session.

The owner wants movies (2026-11-11). v1 is TV only. This plan adds movies to the subtitle fixer, and through it to the Bazarr feature (`BAZARR-PLAN.md`, phase 3). Read `SPEC.md` and `CLAUDE.md` first; every non-negotiable still holds unless this file says otherwise.

## What stays the same
- **Eligibility** (`SPEC.md` section 4): English audio, and a sidecar named exactly `<video>.en.srt`, `.eng.srt`, `.en.hi.srt` or `.eng.hi.srt`, one per item. The `done` fingerprint is video path, size and mtime. Reasons and statuses are unchanged.
- **Job flow** (`JOB-FLOW.md`): backup with journal, run, verify, swap, refresh, restore on any failure. `JobRunner` only needs "a video with facts", not "an episode".
- **Queue:** one shared queue, one SubSync at a time, caps, cancel and visibility rules (D19, D24, D25). Movies and episodes share the queue.
- **State:** `synced_items` is keyed by item id; no schema change.

## What changes
### Domain and library
- `NodeKind.Movie`. `ILibraryFacade` gains movie support:
  - `GetMovieLibraries` is folded into `GetTvLibraries` (renamed `GetLibraries`), returning both `tvshows` and `movies` libraries with a `libraryType`.
  - `GetChildren(movieLibrary)` pages movies sorted by name (`IncludeItemTypes = [Movie]`, `Recursive = true`, `IsVirtualItem = false`). Never the whole library.
  - `GetEpisode` becomes `GetVideo(userId, id)`, returning `EpisodeFacts` (renamed `VideoFacts`) for an episode or a movie.
  - `Search` includes movies.
- `JellyfinLibraryFacade`: `Movie` items map to `NodeKind.Movie`. Their facts come from `IMediaSourceManager.GetMediaStreams` exactly like episodes.

### Selection rules (owner to confirm the first two)
1. **No mixing:** a request is either episodes/seasons of one series, or movies. Mixed requests get `400 mixed_selection`.
2. **Movies need no series lock:** any movies from libraries the user can access, up to `MaxEpisodesPerRequest`. That setting is renamed in the UI to "Most items per request"; the property name is kept for config compatibility.
3. Libraries are still rejected as selection ids (`unsupported_item_type`). A user picks movies, not a whole movie library.

### API (`API.md`)
- `GET /Browse`: library rows carry `libraryType: "tvshows" | "movies"`. A movie library lists `type: "Movie"` rows with the same eligibility fields as episodes, plus `productionYear`.
- `GET /Search`: may return `Movie` rows.
- `POST /Jobs/Preview` and `POST /Jobs`: accept movie ids. For movies, `seriesId`/`seriesName` are null and the preview reads "12 movies selected, 9 will be fixed".
- `GET /Status`: movie items show "Title (Year)" and `seriesName: null`.

### Page (`UI.md`)
- Movie libraries appear next to TV libraries with a film icon. Opening one shows a paged movie list with checkboxes, badges and the queue overlay, like a season.
- The series lock does not apply to movies. Selecting a movie locks the selection to movies (TV rows dimmed with "Clear your selection to choose episodes"), and selecting an episode locks it to that series as today.
- Mock data: add one movie library with about 30 movies in mixed states.

### Bazarr (phase 3 of `BAZARR-PLAN.md`)
- Map a Jellyfin movie to Radarr through Bazarr: by path first (with the path mappings), then by TMDB or IMDb id (`Movie.ProviderIds`), then "Bazarr doesn't know this movie".
- Verified on the local Bazarr: `GET /api/providers/movies?radarrid=N` (search) and `POST /api/providers/movies` (download). Fields are in `LOCAL-TEST-ENV.md`.
- "Find subtitles" only on movies without an English sidecar (D28), like episodes.

## Tests
- `SelectionValidator`: movies accepted; mixed rejected; cap counts eligible movies; libraries still rejected.
- `EligibilityService`: unchanged rules on a movie.
- Facade: movie paging and search (fake library).
- `JobRunner` fault injection: one run on a movie (the code path is shared, but prove it).
- Page in mock mode: movie library, selection lock between movies and TV, queue names.

## Order of work
1. Backend: facade, validator, API, tests. Can start now.
2. Page plus mock data.
3. Live check on Testflix. Its **Film** library already has one movie with `.srt` files (`LOCAL-TEST-ENV.md`); SubSync must be installed for a real run.
4. Bazarr movie search and download (the Bazarr session), after step 1 lands.

## Docs to update in the same change
`SPEC.md` (purpose, scope, eligibility wording "episode or movie"), `CLAUDE.md` non-negotiable 1 (add the movie rule), `API.md`, `UI.md`, `ARCHITECTURE.md` (facade), `DECISIONS.md` (record the two owner confirmations), `ROADMAP.md` (a "Movies" milestone before M5).
