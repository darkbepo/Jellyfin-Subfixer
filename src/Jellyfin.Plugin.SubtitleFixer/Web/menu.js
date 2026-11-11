/* Subtitle Fixer: adds an avatar-menu entry below Profile (M0-SPIKES.md S1).
   Injected into jellyfin-web's index.html by File Transformation. No dependencies. */
(function () {
    'use strict';
    if (window.__subtitleFixerMenu) { return; }
    window.__subtitleFixerMenu = true;

    var ITEM_ID = 'subtitle-fixer-menu-item';
    var script = document.currentScript;
    var apiRoot = new URL('./', script && script.src ? script.src : new URL('../SubtitleFixer/menu.js', document.baseURI)).href;
    var pageUrl = apiRoot + 'Page';
    var allowedFor = null;   // token the "allowed" answer belongs to
    var allowed = false;
    var asking = false;

    function token() {
        try {
            var creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
            var servers = (creds.Servers || []).filter(function (s) { return s && s.AccessToken; });
            servers.sort(function (a, b) { return (b.DateLastAccessed || 0) - (a.DateLastAccessed || 0); });
            return servers.length ? servers[0].AccessToken : null;
        } catch (e) {
            return null;
        }
    }

    // Ask the server once per token whether this user may use the plugin; the entry is shown only if so.
    function checkAllowed(current) {
        if (asking || allowedFor === current) { return; }
        asking = true;
        fetch(apiRoot + 'Me', { headers: { 'Authorization': 'MediaBrowser Token="' + current + '"' } })
            .then(function (r) { return r.ok ? r.json() : { allowed: false }; })
            .then(function (me) { allowed = !!me.allowed; })
            .catch(function () { allowed = false; })
            .then(function () { allowedFor = current; asking = false; schedule(); });
    }

    function closeMenu() {
        var backdrop = document.querySelector('#app-user-menu .MuiBackdrop-root');
        if (backdrop) { backdrop.click(); return; }
        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    }

    function icon() {
        // Own drawing: a caption box with two text lines.
        return '<svg class="MuiSvgIcon-root MuiSvgIcon-fontSizeMedium" focusable="false" aria-hidden="true" viewBox="0 0 24 24" width="24" height="24">' +
            '<path fill="currentColor" d="M4 5h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2zm0 2v10h16V7H4zm2 6h7v2H6v-2zm9 0h3v2h-3v-2zm-9-3h3v2H6v-2zm5 0h7v2h-7v-2z"/></svg>';
    }

    function inject() {
        var current = token();
        var existing = document.getElementById(ITEM_ID);
        if (!current) { if (existing) { existing.remove(); } return; }
        if (allowedFor !== current) { checkAllowed(current); return; }
        if (!allowed) { if (existing) { existing.remove(); } return; }

        var menu = document.getElementById('app-user-menu');
        var list = menu && (menu.querySelector('ul[role="menu"]') || menu.querySelector('ul'));
        if (!list) { return; }
        if (existing && existing.parentNode === list) { return; }
        if (existing) { existing.remove(); }

        var profile = list.querySelector('a[href*="/userprofile"]');
        if (!profile) { return; }

        var item = profile.cloneNode(true);
        item.id = ITEM_ID;
        item.setAttribute('href', pageUrl);
        item.removeAttribute('aria-current');
        item.classList.remove('Mui-selected', 'Mui-focusVisible', 'active');
        var iconBox = item.querySelector('.MuiListItemIcon-root');
        if (iconBox) { iconBox.innerHTML = icon(); }
        var text = item.querySelector('.MuiListItemText-root .MuiTypography-root') || item.querySelector('.MuiListItemText-root');
        if (text) { text.textContent = 'Subtitle Fixer'; }
        item.addEventListener('click', function (e) {
            // Ctrl/Cmd/Shift/middle click keeps the browser's own behaviour (full page in a new tab or window).
            if (e.ctrlKey || e.metaKey || e.shiftKey || e.button === 1) { closeMenu(); return; }
            e.preventDefault();
            e.stopPropagation();
            closeMenu();
            openOverlay();
        });
        profile.parentNode.insertBefore(item, profile.nextSibling);
    }

    // ---------- pop-up over Jellyfin: the page in an iframe, so Jellyfin stays where it was ----------
    var OVERLAY_ID = 'subtitle-fixer-overlay';
    var lastFocus = null;

    function addStyles() {
        if (document.getElementById(OVERLAY_ID + '-style')) { return; }
        var css = document.createElement('style');
        css.id = OVERLAY_ID + '-style';
        css.textContent =
            '#' + OVERLAY_ID + '{position:fixed;inset:0;z-index:100000;display:flex;align-items:center;justify-content:center;' +
            'background:rgba(0,0,0,.6);animation:sfFade .15s ease-out}' +
            '#' + OVERLAY_ID + ' .sf-panel{position:relative;width:min(1100px,94vw);height:min(900px,90vh);border-radius:12px;overflow:hidden;' +
            'background:#0f1113;box-shadow:0 20px 60px rgba(0,0,0,.6);border:1px solid #2a3036}' +
            '#' + OVERLAY_ID + ' iframe{display:block;width:100%;height:100%;border:0;background:#0f1113}' +
            '#' + OVERLAY_ID + ' .sf-close{position:absolute;top:10px;right:10px;z-index:1;width:36px;height:36px;border-radius:50%;' +
            'border:1px solid #2a3036;background:#1f2327;color:#e8eaed;font:20px/1 system-ui,sans-serif;cursor:pointer}' +
            '#' + OVERLAY_ID + ' .sf-close:focus-visible{outline:2px solid #8ab8ff;outline-offset:2px}' +
            '@media (max-width:640px){#' + OVERLAY_ID + ' .sf-panel{width:100vw;height:100vh;height:100dvh;border-radius:0;border:0}}' +
            '@keyframes sfFade{from{opacity:0}to{opacity:1}}' +
            '@media (prefers-reduced-motion:reduce){#' + OVERLAY_ID + '{animation:none}}';
        document.head.appendChild(css);
    }

    function openOverlay() {
        if (document.getElementById(OVERLAY_ID)) { return; }
        addStyles();
        lastFocus = document.activeElement;
        var overlay = document.createElement('div');
        overlay.id = OVERLAY_ID;
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', 'Subtitle Fixer');
        var panel = document.createElement('div');
        panel.className = 'sf-panel';
        var close = document.createElement('button');
        close.type = 'button';
        close.className = 'sf-close';
        close.setAttribute('aria-label', 'Close Subtitle Fixer');
        close.title = 'Close (Esc)';
        close.textContent = '×';
        close.addEventListener('click', closeOverlay);
        var frame = document.createElement('iframe');
        frame.title = 'Subtitle Fixer';
        frame.src = pageUrl + '?embedded=1';
        panel.appendChild(close);
        panel.appendChild(frame);
        overlay.appendChild(panel);
        overlay.addEventListener('mousedown', function (e) { if (e.target === overlay) { closeOverlay(); } });
        document.addEventListener('keydown', onKey, true);
        document.body.appendChild(overlay);
        document.documentElement.style.overflow = 'hidden';
        frame.addEventListener('load', function () { try { frame.contentWindow.focus(); } catch (err) { /* same origin; ignore */ } });
    }

    function closeOverlay() {
        var overlay = document.getElementById(OVERLAY_ID);
        if (!overlay) { return; }
        overlay.remove();
        document.removeEventListener('keydown', onKey, true);
        document.documentElement.style.overflow = '';
        if (lastFocus && lastFocus.focus) { try { lastFocus.focus(); } catch (err) { /* gone */ } }
    }

    function onKey(e) { if (e.key === 'Escape') { e.stopPropagation(); closeOverlay(); } }

    // The page inside asks to close (its Close button, or Esc pressed inside it).
    window.addEventListener('message', function (e) {
        if (e.origin === window.location.origin && e.data && e.data.type === 'subtitle-fixer-close') { closeOverlay(); }
    });

    var pending = false;
    function schedule() {
        if (pending) { return; }
        pending = true;
        window.requestAnimationFrame(function () { pending = false; inject(); });
    }

    // React re-renders the menu list; put the entry back whenever the DOM changes.
    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
    schedule();
})();
