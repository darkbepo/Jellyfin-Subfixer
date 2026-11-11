# Web page behavior

Vanilla JS and CSS served from embedded resources (`index.html`, `app.js`, `styles.css`). No build step, no external CDNs. Layout must work on phone width.

## Entry and auth
- **Menu item:** "Subtitle Fixer" in the avatar menu, added by `menu.js`, which File Transformation injects into `index.html` (`M0-SPIKES.md` S1).
  - The script finds `#app-user-menu ul[role=menu]`, clones the Profile item (`a[href*="/userprofile"]`), sets its icon and text, and inserts it right after Profile.
  - A `MutationObserver` puts the entry back when React re-renders the list.
  - On click it closes the menu (click `#app-user-menu .MuiBackdrop-root`) and goes to `../SubtitleFixer/Page`, a plain relative URL so a base URL keeps working.
- **Auth:** the page reads `localStorage["jellyfin_credentials"]` (same origin), takes the `Servers` entry with the latest `DateLastAccessed` that has an `AccessToken`, and calls the API with the `Authorization: MediaBrowser … Token="…"` header (S2). It calls `GET /Me` first. No token or `401`: link back to Jellyfin login. Never put the token in a URL.
- "Back to Jellyfin" link always visible. Neutral dark styling, no dependency on Jellyfin themes.
- The disclaimer "Only fixes English subtitles on English audio." is always visible under the title.

## Layout
Title, disclaimer, search box, breadcrumb (Libraries > Library > Series > Season), list, footer bar. While a job runs the footer becomes the progress panel.

## Explorer
- Folder-like rows: icon, name, secondary text (seasons: "24 episodes, 3 synced"), status badge (Synced, Attempted, Not eligible with reason on hover/long-press).
- Libraries and series have no checkbox; click opens them. Seasons and episodes have checkboxes; clicking the season name opens it.
- Paged loading (infinite scroll or "load more"); never fetch a whole library.
- Live search: debounce about 250 ms, results are series and episodes; opening a result navigates like browsing.

## Selection model
- State: `selected: Set<episodeId>`, `seasonChecks` derived, `lockedSeriesId | null`.
- Checking a season selects all its eligible episodes (load the season's episodes if not loaded). Season checkbox shows unchecked, indeterminate or checked.
- Selection persists while navigating within the locked series, and is cleared by "Clear selection" or after Start.
- **Series lock**: when `selected` is non-empty, every item of a different series is dimmed with its checkbox disabled (tooltip "Clear your selection to choose another series"). It stays browsable. The lock releases when `selected` is empty.
- Ineligible episodes cannot be checked. Attempted ones can.

## Footer
After each selection change (debounce about 300 ms) call `/Jobs/Preview` and show "47 selected, 12 already synced (skipped)". Start is disabled when `eligible` is 0 or the API returns an error; show the API's message (cap exceeded, queue full, etc.).

## Progress panel
Polls `/Status` every 1 s while running and every 5 s when idle and the tab is visible; stops when hidden. Shows overall bar (done / total), elapsed and ETA, current episode name with an indeterminate working indicator, and a Cancel button. Per-row status in the explorer updates (queued, syncing, done, check, failed). Reopening the page mid-job shows the running job.

When finished: summary "12 done, 1 check, 0 failed" with the non-done episodes and their reasons, and a "Clear" button. If another user's job is running, show "Queue: N episodes ahead".

## Errors
Map API error codes to plain messages (`API.md`). `subsync_unavailable`: "SubSync is not configured. Ask an administrator." Network error: retry banner.

## Mock mode
`?mock=1` runs against `mock-data.js` (3 libraries, 5 series, one season with 40 episodes, a mix of eligible, synced and ineligible) and simulates progress, so Track B can work without the backend.

## Accessibility
Keyboard navigable (arrows, space toggles checkbox, enter opens), visible focus, labels on checkboxes, status not conveyed by color alone.
