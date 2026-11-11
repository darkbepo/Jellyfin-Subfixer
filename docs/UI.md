# Web page behavior

Vanilla JS and CSS served from embedded resources (`index.html`, `app.js`, `styles.css`). No build step, no external CDNs. Layout must work on phone width.

## Entry and auth
- **Menu item:** "Subtitle Fixer" in the avatar menu, added by `menu.js`, which File Transformation injects into `index.html` (`M0-SPIKES.md` S1).
  - The script finds `#app-user-menu ul[role=menu]`, clones the Profile item (`a[href*="/userprofile"]`), sets its icon and text, and inserts it right after Profile.
  - A `MutationObserver` puts the entry back when React re-renders the list.
  - On click it closes the menu (click `#app-user-menu .MuiBackdrop-root`) and goes to `../SubtitleFixer/Page`, a plain relative URL so a base URL keeps working.
- **Auth:** the page reads `localStorage["jellyfin_credentials"]` (same origin), takes the `Servers` entry with the latest `DateLastAccessed` that has an `AccessToken`, and calls the API with the `Authorization: MediaBrowser … Token="…"` header (S2). It calls `GET /Me` first. No token or `401`: link back to Jellyfin login. Not allowed: show the message from `/Me` ("isn't set up yet" until an admin sets the SubSync folder, or "not allowed"). Never put the token in a URL.
- "Back to Jellyfin" link always visible. Neutral dark styling, no dependency on Jellyfin themes.
- The disclaimer "Only fixes English subtitles on English audio." is always visible under the title.

## Layout
Title, disclaimer, search box, breadcrumb (Libraries > Library > Series > Season), list, queue panel, footer bar.
- The **footer bar** is fixed to the bottom: the selection summary, Clear selection and Start.
- The **queue panel** sits under the list. It shows whenever the shared queue has anything in it (running, queued or finished), not only while the viewer's own job runs, because the queue is shared (D19). The footer stays usable meanwhile, so a user can add more while a job runs.
- Admins also get a collapsed **Diagnostics** section (startup state, state.db test row, SubSync check, request timings; used for `M0-LIVE-CHECKS.md` Part B).

## Explorer
- Folder-like rows: icon, name, secondary text (seasons: "24 episodes, 3 synced"), status badge (Synced, Attempted, Not eligible with reason on hover/long-press).
- Libraries and series have no checkbox; click opens them. Seasons and episodes have checkboxes; clicking the season name opens it.
- Paged loading: 50 per page (25 in mock mode). A "Load more (N left)" button, which also loads by itself when it scrolls into view. Never fetch a whole library.
- Live search: debounce about 250 ms, 2+ characters, results are series and episodes (one page of 30; the hint says when there are more). Opening a result navigates like browsing: a series opens the series; an episode's name opens its season, pages until that episode is loaded, scrolls to it and highlights it. Esc clears the search.
- Episode badges: Synced, Previously attempted (with the last message), "Not eligible: <reason>" (the reason is in the badge text, so it works without hover or long-press), and the live queue status (Queued, Syncing…, Fixed, Check, Failed, Cancelled, Skipped). Episodes already queued or running can't be ticked again.

## Selection model
- State: `selected: Set<episodeId>`, `seasonChecks` derived, `lockedSeriesId | null`.
- Checking a season selects all its eligible episodes (load the season's episodes if not loaded). Season checkbox shows unchecked, indeterminate or checked.
- Selection persists while navigating within the locked series, and is cleared by "Clear selection" or after Start.
- **Series lock**: when `selected` is non-empty, every item of a different series is dimmed with its checkbox disabled (tooltip "Clear your selection to choose another series"). It stays browsable. The lock releases when `selected` is empty.
- Ineligible episodes cannot be checked. Attempted ones can.

## Footer
After each selection change (debounce about 300 ms) call `/Jobs/Preview` and show for example "40 selected in The Long Show, 27 will be fixed, 13 skipped (5 already synced), 4 tried before." Start shows the count ("Start (27)") and is disabled when `eligible` is 0 or the API returns an error; the API's message is shown in red (cap exceeded, queue full, etc.).
- A fully ticked season is sent as the **season id** (so the server reports its skipped episodes); partly ticked seasons are sent as episode ids.
- After Start the selection clears and the footer says how many were queued and skipped.

## Progress panel
Polls `/Status` every 1 s while running and every 5 s when idle and the tab is visible; stops when hidden. Shows overall bar (done / total), elapsed and ETA, current episode name with an indeterminate working indicator, and a Cancel button. Per-row status in the explorer updates (queued, syncing, done, check, failed). Reopening the page mid-job shows the running job.

**The queue is shared.** There is one global queue and one SubSync process at a time. When a second person presses Start, their episodes go to the end of the same queue. The progress panel always shows that whole queue to every allowed user who opens the page, not only to whoever started it:
- the current episode;
- the totals;
- the list of queued items, with the viewer's own items marked "yours".

Episodes from libraries the viewer can't access are shown as "Episode in another library". When the viewer's own items are behind someone else's, show "Queue: N episodes ahead of yours". Cancel only cancels the viewer's own items, except for admins, who can cancel everything (see `API.md`).

When finished: summary "12 done, 1 check, 0 failed" with the non-done episodes and their reasons, and a "Clear" button.

## Errors
Map API error codes to plain messages (`API.md`). `subsync_unavailable`: "SubSync is not configured. Ask an administrator." Network error: retry banner.

## Mock mode
`?mock=1` runs against `mock-data.js` (3 libraries, 5 series, one season with 40 episodes, a mix of eligible, synced, attempted and ineligible) and simulates progress, so Track B can work without the backend. The mock starts with another user's hidden episode running, so the shared-queue display is visible at once.
- Extra switches: `&user=member` (not admin), `&user=denied` (not allowed), `&subsync=off` (Start fails with `subsync_unavailable`), `&slow=1` (500 ms latency).
- Without Jellyfin: `powershell -ExecutionPolicy Bypass -File build\dev-server.ps1` serves the page at `http://localhost:5178/SubtitleFixer/Page?mock=1` (also `.claude/launch.json` "page-mock").
- The plugin serves `mock-data.js` too, so `?mock=1` also works on a real server for demos; it never touches real data.

## Accessibility
- Keyboard: the list is one tab stop (roving focus). Up/Down move between rows (Up from the first row goes to search, Down from search enters the list), Home/End, Space toggles the row's checkbox, Enter or Right opens a library, series or season (and a search result), Left or Backspace goes up one level, `/` jumps to search.
- Visible focus ring on everything; labelled checkboxes ("Select S01E04 Title"); status always has text, never color alone; the progress bar has `role=progressbar` with a value; the queue summary and footer are live regions.
- Works at 375 px: badges wrap under the name, footer buttons fill the width. Respects reduced motion.
