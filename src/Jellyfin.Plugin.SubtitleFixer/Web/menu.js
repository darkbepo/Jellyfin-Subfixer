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
            e.preventDefault();
            e.stopPropagation();
            closeMenu();
            window.location.href = pageUrl;
        });
        profile.parentNode.insertBefore(item, profile.nextSibling);
    }

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
