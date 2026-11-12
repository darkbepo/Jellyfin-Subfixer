# Milestone 0: spikes

Goal: replace assumptions with verified facts before building. One session, needs the .NET 10 SDK, internet, a Jellyfin 12 test server (File Transformation installed), SubSync installed, and one test season of episodes with `.en.srt` sidecars. Record results under each spike's **Findings**. If a result breaks a design assumption, fix the docs and tell the owner before M1.

Reference implementations to read (check licenses before copying any code): File Transformation and Plugin Pages (IAmParadox27), Achievement Badges for Jellyfin (ZL154; avatar-menu entry on 12), Jellyfin Enhanced (n00bcodr; avatar-menu entry on 12), the official Jellyfin plugin template.

**How findings are marked.** "From source" means it was read from the code at the versions below, not run on a server. Each one still needs the live check listed with it. The Windows checklist for the owner collects those checks.

Sources read (2026-11-11): `jellyfin/jellyfin` tag `v12.4`, `jellyfin/jellyfin-web` tag `v12.4` (uses `jellyfin-apiclient` 1.11.0), File Transformation `main` (release `3.0.3.0`, asset `Release-12.4.0.zip`), Plugin Pages `main`, Achievement Badges `main`, Jellyfin Enhanced `main`, `jellyfin/jellyfin-plugin-template` `main`.

Licenses: File Transformation, Plugin Pages, Jellyfin Enhanced and the plugin template are GPL-3.0. Achievement Badges is MIT. No code has been copied; the findings describe behavior only.

## S0 Environment
Record exact Jellyfin server version, jellyfin-web version, .NET SDK, SubSync version (`subsync-cli version`), GPU. Scaffold from the official plugin template targeting net10.0 and `Jellyfin.Controller` 12.x; confirm the plugin loads. Run `subsync-cli check`.
**Findings:**
- Latest releases: Jellyfin server and jellyfin-web `12.4` (2026-10-26). Earlier 12.x: 12.0 (2026-09-08), 12.1, 12.2, 12.3.
- NuGet has `Jellyfin.Controller` and `Jellyfin.Model` `12.0.0` to `12.4.0`. Target `12.4.0` to match the owner's server.
- Dev machine: .NET SDK `10.0.404`; a `net10.0` class library builds clean.
- The official template is still on `net9.0`, `Jellyfin.Controller` `10.11.5` and `targetAbi` `10.11.0.0`. The scaffold has to raise all three: `net10.0`, `12.4.0`, `targetAbi` `12.0.0.0`. Achievement Badges already ships 12.x builds with `targetAbi` `12.0.0.0`.
- The template references Jellyfin packages with `<ExcludeAssets>runtime</ExcludeAssets>`. Keep that; the server supplies those assemblies.
- **Live, 2026-11-11 on the owner's local test server "Testflix" (Jellyfin 12.4.0, Windows tray app):** the plugin built this way loads (`Loaded plugin: "Subtitle Fixer" "0.1.0.0"`), with no errors in the log once `meta.json` is written without a BOM.
  - Found live: Jellyfin's `System.Text.Json` rejects a UTF-8 BOM in `meta.json` ("'0xEF' is an invalid start of a value"), then rewrites the file itself. Windows PowerShell 5.1's `-Encoding utf8` always writes a BOM, so `build/package.ps1` now writes it BOM-free.
  - The plugin's data folder is `<datadir>\plugins\Jellyfin.Plugin.SubtitleFixer\` (named after the assembly), not under `configurations`.
- **Still to check (owner):** SubSync version, GPU and `subsync-cli check` (SubSync isn't installed on Testflix).

## S1 Menu injection through File Transformation
How does a plugin register a transformation (its API or interface)? Inject a script into the served web client that adds a "Subtitle Fixer" item to the avatar menu (MUI menu, so use a MutationObserver), placed below "Profile". Pass if: appears for admin and non-admin on desktop and mobile layouts, survives navigation, is absent when the plugin is disabled, and uses the `#/` route style if it links into the SPA. Fallbacks: JavaScript Injector plugin, or a direct page URL.
**Findings (from source):**
- **Registration is by reflection, not a package reference.** Find the loaded assembly whose name contains `.FileTransformation`, get type `Jellyfin.Plugin.FileTransformation.PluginInterface`, and call its static `RegisterTransformation(JObject payload)`. The README says a direct reference does not work because plugins load in separate contexts. `RemoveTransformation(Guid id)` also exists.
- **Payload fields:** `id` (our own fixed GUID), `fileNamePattern`, `callbackAssembly` (`typeof(X).Assembly.FullName`), `callbackClass` (full type name), `callbackMethod`. There are also `transformationPipe` and `transformationEndpoint`; we don't need them.
- **Matching:** `fileNamePattern` is matched against the path after `/web/`, first as an exact key and then as a regex (2 s timeout). `/web/` and `/web` map to `index.html`. Use `index.html`.
- **Callback:** a public static method with one parameter. File Transformation converts `{ "contents": "<file text>" }` into that parameter's type, so a small class of our own with a `Contents` string property works. The method returns the new file text as a `string`. If it returns `null` or throws, the original is served unchanged. Other plugins' transforms on the same file run in sequence.
- **Newtonsoft must not be bundled.** `RegisterTransformation` takes a Newtonsoft `JObject`. File Transformation's 12.4 zip has no `Newtonsoft.Json.dll`, so it uses the server's copy (server pins `13.0.4`). If our plugin shipped its own copy, our `JObject` would be a different type and the call would fail. We avoid the dependency: take the `JObject` type from `RegisterTransformation`'s parameter by reflection and call its static `Parse(string)`. See D16.
- **Response handling:** File Transformation buffers the response, strips compression and recompresses it, and sets `ETag` with `Cache-Control: no-cache`. Our script tag therefore shows up without users clearing their cache.
- **What to inject:** one tag before `</body>` in `index.html`: `<script plugin="SubtitleFixer" src="../SubtitleFixer/menu.js" defer></script>`. The path is relative to `/web/`, so it works under a base URL. The script itself comes from our own controller.
- **Avatar menu on 12.4** (`src/components/toolbar/AppUserMenu.tsx`): an MUI `Menu` with `id="app-user-menu"` and `keepMounted`, so its `ul[role=menu]` stays in the page while closed. The first item is Profile, rendered as an `<a>` linking to `/userprofile?userId=…` (`a[href*="/userprofile"]`). Admins also see Dashboard and Metadata. Routing is a hash router (`createHashRouter`).
- **Injection approach:**
  1. Clone the Profile `<a>` so the entry gets the MUI classes, change its icon and text, and insert it right after Profile.
  2. React does not own the clone, so a click handler has to close the menu itself (click `#app-user-menu .MuiBackdrop-root`) and then navigate.
  3. Keep a `MutationObserver` on `document.body` running so the entry is put back if React re-renders the list.
  4. The entry opens our plugin-served page, not an SPA route, so it uses a plain relative URL (`../SubtitleFixer/Page`) rather than `#/`.
  - Achievement Badges (MIT) does the same on 12.0; we follow the approach without copying code.
- **Disabled plugin:** if the plugin is disabled, it never loads (after the restart Jellyfin requires), never registers, and the tag isn't injected.
- **Design note for the owner:** neither Achievement Badges nor Jellyfin Enhanced uses File Transformation on 12; both inject through their own `IStartupFilter` middleware. File Transformation still works on 12.4 and has 12.x releases, so D3 stands. The middleware approach is a known fallback if it stops keeping up with Jellyfin releases.
- **Live, 2026-11-11 on Testflix (File Transformation 3.0.3.0, alongside JavaScript Injector, Jellyfin Enhanced and KefinTweaks):**
  - Registration by reflection works ("Registering transformation for 'index.html'"), and `/web/index.html` contains our `<script plugin="SubtitleFixer" …>` tag.
  - The entry appears directly below "Profil" (Danish UI), styled like its neighbours, with the other plugins' entries further down. Clicking it closes the menu and opens `/SubtitleFixer/Page`. Desktop layout, admin user.
- **Still to check (owner):**
  - The entry appears below Profile for admin and non-admin, on desktop width and phone width.
  - It survives navigating between pages and opening the menu several times.
  - It is absent after disabling the plugin and restarting.
  - It also works in the legacy layouts if those are still selectable on 12.4.

## S2 Plugin-served page and auth
Serve an embedded HTML page from a controller route. Read the token from jellyfin-web's stored credentials and call a plugin endpoint. Jellyfin 12 switched legacy authorization off, so use the current `Authorization: MediaBrowser ...` header and confirm `401` handling. Pass if an authenticated call returns the user's id. Fallback: Plugin Pages (`PluginInterface.RegisterPage`; GPL-3.0, extra dependency).
**Findings (from source):**
- **Header parsing** (`Jellyfin.Server.Implementations/Security/AuthorizationContext.cs`):
  - The token is the `Token="…"` field of `Authorization: MediaBrowser Client="…", Device="…", DeviceId="…", Version="…", Token="…"`.
  - `EnableLegacyAuthorization` (`ServerConfiguration`) defaults to `false`. That disables the `Emby` scheme, `X-Emby-Authorization`, `X-Emby-Token`, `X-MediaBrowser-Token` and `?api_key=`.
  - `?ApiKey=<token>` in the query string is still accepted with legacy auth off.
- **Policies** (`MediaBrowser.Common.Api.Policies`, in the `Jellyfin.Common` package): plain `[Authorize]` for any signed-in user, and `[Authorize(Policy = Policies.RequiresElevation)]` for admin-only endpoints. Without a valid token the auth middleware returns `401` before our code runs.
- **Current user:** in a controller, use the claim `Jellyfin-UserId`. That's `Jellyfin.Api.Constants.InternalClaimTypes.UserId`; `Jellyfin.Api` isn't on NuGet, so keep the string as our own constant. Alternatively inject `MediaBrowser.Controller.Net.IAuthorizationContext` and call `GetAuthorizationInfo(Request)`, which returns the `User`. Then apply our `AllowedUsers` check.
- **Stored credentials:** jellyfin-web keeps them in `localStorage["jellyfin_credentials"]` as JSON `{ Servers: [ { Id, AccessToken, UserId, ManualAddress, LocalAddress, DateLastAccessed, … } ] }`. Our page is on the same origin, so it can read this. Pick the entry with the latest `DateLastAccessed` that has an `AccessToken`; if none, show the "sign in to Jellyfin" link.
- **Serving the page:** a controller action marked `[AllowAnonymous]` at `GET /SubtitleFixer/Page` returns the embedded `index.html`; `app.js` and `styles.css` come the same way. The HTML holds no data, and every data call is authenticated. Achievement Badges serves its standalone page the same way. Use relative URLs throughout so a base URL keeps working. Don't put the token in the URL.
- **Live, 2026-11-11 on Testflix:** `GET /SubtitleFixer/Me`, `/Browse` and `/Status` return `401` with no token and with a made-up token. Signed in, the page reads the token from `jellyfin_credentials` and gets 200 from every endpoint (14–51 ms). It showed "Signed in as <the test admin> (administrator)" (that line was dropped on 2026-11-12) and the admin diagnostics. Static routes (`/Page`, `/Page/app.js`, `/menu.js`) are served anonymously.
- **Still to check (owner):**
  - Open the page while signed in: `GET /SubtitleFixer/Me` returns your user id.
  - Signed out, or with a bad token: it returns `401` and the page shows the sign-in link.
  - A non-admin gets `403` from an admin endpoint.

## S3 Library queries at scale
Browse libraries -> series -> seasons -> episodes with paging; search by term with paging; respect the user's library access. Read audio languages and external subtitle streams from media streams. Time a season listing and a search on the real ~40,000-episode library. Check `VirtualFolderInfo.ItemId` behavior on 12. Pass if a season lists in well under a second and search feels live.
**Findings (from source):**
- **Libraries:** `IUserViewManager.GetUserViews(new UserViewQuery { User = user })` returns only the views that user can see. Keep the ones with `CollectionType == tvshows`.
- **Paged children and search:**
  - `ILibraryManager.GetItemsResult(InternalItemsQuery)` returns `QueryResult<BaseItem>` with `TotalRecordCount`.
  - Useful query fields: `User` (applies library access and parental rules), `ParentId`, `AncestorIds`, `IncludeItemTypes` (`BaseItemKind.Series`, `Season`, `Episode`), `Recursive`, `StartIndex`, `Limit`, `EnableTotalRecordCount`, `OrderBy`, `SearchTerm`, `IsVirtualItem = false`.
  - Use `SearchTerm` with `IncludeItemTypes = [Series, Episode]`, `Recursive = true` and `Limit` for search.
  - `GetCount(query)` is also available.
- **Seasons and episodes:** `Series.GetSeasons(user, dtoOptions)` and `Season.GetEpisodes(user, dtoOptions, shouldIncludeMissingEpisodes: false)` handle a single parent. They don't page, which is fine for one season.
- **Single item with access check:** `ILibraryManager.GetItemById<T>(id, user)`.
- **Episode fields:** `Episode.SeriesId`, `SeasonId`, `SeriesName`, `IndexNumber`, `ParentIndexNumber`, `Path`.
  - For the fingerprint, read size and modified time from the file system (`FileInfo`), not `BaseItem.Size` or `DateModified`. Those are database copies that may be stale until the next scan.
- **Media streams:**
  - `IMediaSourceManager.GetMediaStreams(itemId)`, or `GetMediaStreams(new MediaStreamQuery { ItemId, Type })`, returns audio and external subtitle streams.
  - The fields we need: `Type`, `Language`, `IsExternal`, `Path`.
  - Each call is one database query per item. A 24-episode season means 24 small queries, so per-season counts in Browse should be computed only for the page being shown.
- **`VirtualFolderInfo.ItemId` is still filled in on 12.4** (`LibraryManager` sets it when a matching collection folder exists). That doesn't matter for us, because user views give us library IDs directly.
- **Live check (owner):** time a season listing and a search on the 40,000-episode library. Confirm a restricted user doesn't see hidden libraries.
- **Search within chosen libraries (D43, 2026-11-12):** with `AllowedLibraryIds` set, search calls `Folder.GetItems(InternalItemsQuery { User, SearchTerm, IncludeItemTypes = [Series, Episode, Movie], Recursive = true, IsVirtualItem = false, StartIndex = 0, Limit = startIndex + limit, EnableTotalRecordCount = true })` on each allowed library returned by `GetUserViews`, then merges. This compiles against `Jellyfin.Controller` 12.4.0 and is the same call that pages a library's series in Browse, but whether a recursive `GetItems` on a collection folder honours `SearchTerm` is not verified live yet (`M0-LIVE-CHECKS.md`, D43). With no libraries chosen, search is unchanged (`ILibraryManager.GetItemsResult`).

## S4 Scanner, `.bak`, refresh
(a) Does a `X.en.srt.bak` next to the video show up in the library or as a subtitle? (b) After replacing the sidecar's content, which validation-only refresh options make Jellyfin serve the new subtitle immediately (request `/Videos/{id}/{mediaSourceId}/Subtitles/{index}/Stream.srt` and compare)? Is there any subtitle cache that needs busting? Record the exact `MetadataRefreshOptions`.
**Findings (from source):**
- **(a) `.bak` files are ignored.** External files are recognized by their last extension (`ExternalPathParser.ParseFile` checks `NamingOptions.SubtitleFileExtensions`). For `X.en.srt.bak` that extension is `.bak`, which is not in the subtitle, audio, lyric or video lists, so it is never a subtitle or a media item.
- **(b) No subtitle cache for external `.srt`.**
  - `SubtitleEncoder.GetReadableFile` returns an external text subtitle the parser supports (SRT included) straight from its `Path`, and the file is read again on every request.
  - The disk cache (`GetSubtitleCachePath`, with a `.meta` size+mtime check) is only for embedded or converted subtitles, and even then it notices a changed file.
  - So the overwritten `.en.srt` is served right away, refresh or not.
- **The refresh barely matters.** `ProbeProvider.HasChanged` re-probes when the video changed or the set of external subtitle paths changed. We keep the same path, so a default refresh is close to a no-op.
- **Refresh to use anyway:** the same call Jellyfin makes after a subtitle upload or download (`SubtitleController`): `providerManager.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(fileSystem)), RefreshPriority.High)`. That is `MetadataRefreshMode.Default` with no replace flags, and it keeps us correct if a future Jellyfin starts caching.
- **Live check (owner):** after a sync, fetch `/Videos/{id}/{mediaSourceId}/Subtitles/{index}/Stream.srt` and compare it with the file on disk. Confirm a `.bak` left during a sync never appears in the item's subtitle list after a library scan.

## S5 SubSync CLI behavior
On one real episode: `--pair`, `--overwrite`, `--suffix`, `--report` with spaces in paths. Confirm the report fields and exit codes in `JOB-FLOW.md`, per-process startup time (model load), behavior when the result file is open elsewhere, and that killing the process tree leaves no orphan processes.
**Findings (live, 2026-11-11, SubSync 1.2.0 from the GitHub release, installed at `C:\SubSync`, CPU only):**
- **Install:** `check` exits 8 until `setup` has run. `setup` downloaded 1.8 GB here, not 3.2 GB, probably because there are no CUDA libraries without an NVIDIA GPU. `check` then exits 0 with "Installation is complete and intact."
- **Paths:** the exact plugin command (`sync --pair V S --overwrite --suffix en --report R`) works with spaces and non-ASCII in the paths. The result overwrites `<video>.en.srt` in place.
  - The result takes the **video's** letter case for its name (`S01e01 … Webdl-1080P.en.srt` replaced `S01E01 … WEBDL-1080p.en.srt`). Harmless on Windows.
- **Report:** `{ subsync_version, finished, exit_code, seconds, summary, items: [ { video, subtitle, status, output, reason, audio_language, seconds, lines_matched, … } ] }`. `SubSyncReport.Parse` reads it as is.
- **Outcomes:**
  - Synced: exit 0, `status: synced`.
  - Wrong subtitle: exit 4, `status: no_match`, nothing written.
- **`--suffix`** accepts only `en`, `eng`, `.en` or `.eng`. `en.hi` gives exit 6 ("invalid choice"). Hearing-impaired files therefore run with the plain tag, and `CliWorker` moves the result over the `.hi` file (D27). It refuses to run if a plain `.en.srt` already exists.
- **Timing (CPU, i7-13850HX):**
  - A 6-minute clip took 96 s.
  - A 22-minute episode takes about 6.5 minutes.
  - Re-running the same file takes 3.4 s (transcript cache).
  - The output does not say GPU or CPU, except the line "Speech recognition: CPU (no usable NVIDIA GPU – slower)" printed during `sync`; `check` doesn't say either.
- **Kill:** killing `subsync-cli.exe` with its tree (it starts 2 `python.exe`) leaves nothing running, and the subtitle is untouched.
  - However, **killing Jellyfin did NOT kill SubSync**: an orphaned run kept going and could have overwritten a subtitle after startup recovery. This is now fixed with a kill-on-close Windows Job Object plus a leftover-run check at startup (D29), and verified live: 0 processes left after hard-killing Jellyfin.
- **Not tested:** the 5-minute wait when the result file is held open by another program.
- **Real-CLI tests:** `RealSubSyncTests` run the real CLI through `CliWorker` when `SUBFIXER_SUBSYNC_FOLDER` and `SUBFIXER_TEST_CLIP` are set. All 5 pass locally, and they're skipped in CI.

## S6 Running under the Jellyfin service account
Launch the CLI from the plugin while Jellyfin runs as a Windows service (and as a tray app). Does it get the GPU (CUDA) or fall back to CPU? Does it have write access to the SubSync folder and media folders? Fallbacks: `ForceCpu`, run Jellyfin as a tray app, grant folder rights.
**Findings:**
- **Tray app (live, 2026-11-11 on Testflix):** Jellyfin runs as the signed-in user, launches the CLI from the plugin, and can write to `C:\SubSync` and the media folders. On this PC (Intel UHD, no NVIDIA) SubSync falls back to CPU by itself; no `ForceCpu` is needed.
- **Still open (owner):** Jellyfin as a Windows **service**, and the GPU on the real server.

## S7 SQLite inside the plugin
Does `Microsoft.Data.Sqlite` load in the plugin's load context (native library included)? Fallback: use the assembly Jellyfin already ships, or an append-only JSONL log plus in-memory dictionary compacted at startup (40,000 records is fine).
**Findings (from source):**
- **How plugins load** (`Emby.Server.Implementations/Plugins`):
  - Each plugin gets its own collectible `PluginLoadContext`, and every `*.dll` in the plugin folder is loaded into it (or only the whitelisted ones if `meta.json` lists `assemblies`).
  - `Load()` returns `null` when the plugin folder can't resolve an assembly, so .NET falls back to the default context: the server's own copies.
  - `LoadUnmanagedDll` isn't overridden, so a native `e_sqlite3.dll` shipped in the plugin folder may not be found.
- **The server already ships SQLite:** Jellyfin 12.4 references `Microsoft.Data.Sqlite` `10.0.14` and `Microsoft.EntityFrameworkCore.Sqlite` `10.0.14` for its own database, so `Microsoft.Data.Sqlite` and the SQLitePCLRaw native library are loaded and initialized in the default context.
- **Result:** the "use the assembly Jellyfin already ships" fallback becomes the plan.
  - Reference `Microsoft.Data.Sqlite` `10.0.x` (not above the server's) with `<ExcludeAssets>runtime</ExcludeAssets>`. Nothing SQLite-related goes in our zip.
  - At runtime we get the server's copy, with its native library already working.
  - Risk: a future Jellyfin could change the major version; `StateStore` hides SQLite so we can switch to the JSONL fallback. See D17.
- **Live, 2026-11-11 on Testflix: passes.** The database is created and migrated to v2 inside Jellyfin, using the server's SQLite with nothing bundled. The diagnostics row was written, then read back after a full Jellyfin restart.

## Exit criteria
All spikes have findings; docs corrected; owner informed of any design change.
