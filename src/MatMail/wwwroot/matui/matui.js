/* ==========================================================================
   MatUi · behaviour
   Dependency-free. Everything is wired by delegated listeners and data-attributes, so server-rendered and dynamically added
   markup behave the same. The public API is window.MatUi (dialog, confirm, alert, prompt, toast, theme, accent).

   Data-attributes
     data-theme-toggle                      the 3-state button (system -> light -> dark); data-theme-save-url posts the choice
     data-theme-set="system|light|dark"     a button of a segmented control in the settings that chooses the theme (aria-pressed follows)
     data-nav-toggle / data-nav-close       open / close the menu drawer (phones)
     data-nav-branch="key"                  the parent of a submenu in the menu (aria-expanded; the choice is remembered per app)
     data-nav-set="id" / data-nav-panel="id"  the menu switcher: an entry of its menu shows the panel of items with the same id;
                                            the menu names the element with the switcher and the panels: data-nav-scope="id". There can be a switcher
                                            under the logo and one at the foot of the menu (both in the scope, both kept in step)
     data-nav-group                         the parent of some menus in the popup of the switcher (aria-expanded, aria-controls = the element with its menus;
                                            the menus carry data-nav-parent="Title of the parent"); choosing a menu opens its group
     data-scrollspy                         an index of #links to sections of the page ("On this page"): the link of the section in view is marked
     data-validation-summary                an alert in a form that lists the fields with an error, each a link to its field (hidden while there are none)
     data-output="id"                       a range input writes its value into the element with this id (the filled part of the track is drawn for every .mat-range)
     data-counter="id"                      a text field writes "length / maxlength" into the element with this id
     data-indeterminate                     a checkbox that starts in the indeterminate state
     data-password-toggle="id"              the eye button of a password field: shows and hides the password (aria-pressed)
     data-selectable                        a table with data-select-row checks, a data-select-all check in its header and a [data-bulk-bar] with [data-bulk-count]
     data-picker="select|date|daterange|time|timerange|colour|tags"
                                            a field that is filled in a panel (FORM-15); the real input inside is .mat-picker__input. Options of the wrapper:
                                            data-placeholder, data-clearable (a button that clears the value), data-min / data-max / data-week-start / data-flex (dates),
                                            data-step / data-hour12 (times), data-swatches (colours), data-search / data-multiple / data-sep (select; the options are
                                            .mat-picker__option with data-value). The texts change with data-pk-<name> on <html>, the format of dates with data-locale.
                                            tags is a field of chips and has no panel: data-sep, data-max (number of tags), data-max-length (of one tag),
                                            data-duplicates (allow a tag twice), data-strict (only the options, which are the suggestions, can be added)
     data-carousel                         a strip of slides (.mat-carousel__track > .mat-carousel__slide) that scrolls and snaps; the script adds Previous, the dots and Next
                                            (LIST-13). data-loop: Next on the last page goes to the first; data-autoplay="ms": it moves by itself (Pause button; it waits
                                            while the pointer or the focus is inside and never starts for a user who asked for less motion). The texts: data-car-<name> on <html>
     data-menu-toggle="id"                  open the context menu #id next to the button; data-menu-place="top|bottom"
     data-dialog-open="id" / data-dialog-close   open / close a <dialog class="mat-dialog">
     data-confirm="Question?"               ask in a dialog before a submit button, a form or a link goes ahead
     data-show-if="#id | name" data-show-value="a,b | * | true"   show a dependent field only when it is needed
     data-copy="text | #id"                 copy to the clipboard
     data-tabs                              a tab group (role=tablist buttons + role=tabpanel panels)
     data-dismiss                           close the alert around it
     data-view-switch="list|gallery"        switch between the two views of a list (remembered per data-view-key)
     data-shortcut="/"                      key that clicks the element, or focuses it when it is a text field (not while typing)
   ========================================================================== */
(function () {
  'use strict';

  var root = document.documentElement;
  var doc = document;

  // ---------------------------------------------------------------------------------------------------- helpers
  function $(sel, ctx) { return (ctx || doc).querySelector(sel); }
  function $$(sel, ctx) { return Array.prototype.slice.call((ctx || doc).querySelectorAll(sel)); }
  function esc(s) { return String(s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  function label(attr, fallback) { return root.getAttribute(attr) || fallback; }
  function icon(name) {
    var sprite = root.getAttribute('data-mat-sprite') || '';
    return '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="' + sprite + '#mat-i-' + name + '"></use></svg>';
  }
  var uid = 0;
  function nextId(prefix) { uid += 1; return prefix + '-' + uid; }

  // ------------------------------------------------------------------------------- scrollbars only while scrolling
  var scrollTimers = new WeakMap();
  doc.addEventListener('scroll', function (e) {
    var el = e.target === doc ? root : e.target;
    if (!el || !el.classList) { return; }
    el.classList.add('is-scrolling');
    clearTimeout(scrollTimers.get(el));
    scrollTimers.set(el, setTimeout(function () { el.classList.remove('is-scrolling'); }, 900));
  }, true);

  // ---------------------------------------------------------------------------------------------------- theme
  // system -> light -> dark -> system. "system" means no data-theme attribute: the CSS follows the OS.
  var THEMES = ['system', 'light', 'dark'];
  var THEME_NAMES = { system: 'System', light: 'Light', dark: 'Dark' };

  function currentTheme() {
    var t = root.getAttribute('data-theme');
    return t === 'light' || t === 'dark' ? t : 'system';
  }

  function applyTheme(theme, persist) {
    if (theme === 'light' || theme === 'dark') { root.setAttribute('data-theme', theme); } else { root.removeAttribute('data-theme'); theme = 'system'; }
    if (persist !== false && !root.hasAttribute('data-preview')) { try { localStorage.setItem('mat-theme', theme); } catch (e) { /* ignore */ } }
    $$('[data-theme-toggle]').forEach(function (btn) {
      var next = THEMES[(THEMES.indexOf(theme) + 1) % THEMES.length];
      btn.setAttribute('data-theme-state', theme);
      var text = 'Theme: ' + THEME_NAMES[theme] + '. Click for ' + THEME_NAMES[next] + '.';
      btn.setAttribute('aria-label', text);
      btn.setAttribute('title', text);
    });
    $$('[data-theme-set]').forEach(function (btn) { btn.setAttribute('aria-pressed', btn.getAttribute('data-theme-set') === theme ? 'true' : 'false'); });
    root.dispatchEvent(new CustomEvent('mat:theme', { detail: { theme: theme } }));
  }

  function cycleTheme(btn) {
    chooseTheme(btn, THEMES[(THEMES.indexOf(currentTheme()) + 1) % THEMES.length]);
  }

  // The 3-state button cycles; a segmented control in the account settings chooses (data-theme-set="light"). Both save the same way.
  function chooseTheme(btn, theme) {
    if (THEMES.indexOf(theme) < 0) { return; }
    applyTheme(theme);
    var url = btn && btn.getAttribute('data-theme-save-url');
    if (url) {
      var body = new URLSearchParams({ theme: theme });
      var token = $('input[name="__RequestVerificationToken"]');
      if (token) { body.set('__RequestVerificationToken', token.value); }
      fetch(url, { method: 'POST', body: body, credentials: 'same-origin' }).catch(function () { /* the choice stays in this browser */ });
    }
  }

  // A free colour: the on-accent colour (white or dark) is the one with the better contrast, like the toolkit does for the apps.
  function luminance(hex) {
    var v = [1, 3, 5].map(function (i) { var c = parseInt(hex.substr(i, 2), 16) / 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); });
    return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2];
  }
  function customAccent(hex) {
    if (!/^#[0-9a-fA-F]{6}$/.test(hex || "")) { return false; }
    var l = luminance(hex);
    var onWhite = 1.05 / (l + 0.05), onInk = (l + 0.05) / (luminance("#0b1020") + 0.05);
    root.style.setProperty("--mat-custom-accent", hex);
    root.style.setProperty("--mat-custom-on-accent", onWhite >= 4.5 || onWhite >= onInk ? "#ffffff" : "#0b1020");
    root.setAttribute("data-accent", "custom");
    root.dispatchEvent(new CustomEvent("mat:accent", { detail: { accent: "custom", color: hex } }));
    return true;
  }

  function setAccent(name) {
    if (name) { root.setAttribute('data-accent', name); } else { root.removeAttribute('data-accent'); }
    try { if (name) { localStorage.setItem('mat-accent', name); } else { localStorage.removeItem('mat-accent'); } } catch (e) { /* ignore */ }
    root.dispatchEvent(new CustomEvent('mat:accent', { detail: { accent: name || null } }));
  }

  // ---------------------------------------------------------------------------------------------- menu drawer (phones)
  function app() { return $('.mat-app'); }
  var drawerOpener = null;

  function setNav(open, opener) {
    var shell = app();
    if (!shell) { return; }
    shell.classList.toggle('is-nav-open', open);
    root.classList.toggle('mat-lock', open);
    $$('[data-nav-toggle]').forEach(function (b) { b.setAttribute('aria-expanded', open ? 'true' : 'false'); });
    var nav = $('.mat-nav', shell);
    if (open) {
      drawerOpener = opener || doc.activeElement;
      var first = nav && $('.mat-nav__scroll:not([hidden]) .mat-nav__item', nav);
      if (first) { first.focus({ preventScroll: true }); }
    } else if (drawerOpener && drawerOpener.focus) {
      drawerOpener.focus({ preventScroll: true });
      drawerOpener = null;
    }
  }

  // ------------------------------------------------------------------------------ menu: submenus and menu sets (SHELL-13, SHELL-15)
  // A parent item is a button (data-nav-branch="key", aria-expanded) that opens its children. The branch with the current page is open
  // (server side: is-open + is-current); what the user opened or closed is remembered per app. A menu set is a panel of items
  // (data-nav-panel="id"); the switcher under the logo (data-nav-set="id" in its menu) shows one panel at a time.
  var BRANCH_KEY = 'mat-nav-branches';

  function branchStore() {
    try { return JSON.parse(localStorage.getItem(BRANCH_KEY) || '{}') || {}; } catch (e) { return {}; }
  }

  function branchId(btn) { return (root.getAttribute('data-app') || '') + ':' + btn.getAttribute('data-nav-branch'); }

  function setBranch(btn, open, remember) {
    var branch = btn.closest('.mat-nav__branch');
    if (!branch) { return; }
    if (!open && branch.contains(doc.activeElement) && doc.activeElement !== btn) { btn.focus({ preventScroll: true }); }
    branch.classList.toggle('is-open', open);
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (remember) {
      var store = branchStore();
      store[branchId(btn)] = open;
      try { localStorage.setItem(BRANCH_KEY, JSON.stringify(store)); } catch (e) { /* ignore */ }
    }
  }

  function restoreBranches() {
    var store = branchStore();
    $$('[data-nav-branch]').forEach(function (btn) {
      var saved = store[branchId(btn)];
      // the branch of the current page stays open whatever was saved
      if (typeof saved === 'boolean' && !btn.closest('.mat-nav__branch').classList.contains('is-current')) { setBranch(btn, saved, false); }
    });
  }

  // The popup of the switcher names the element that holds the switcher(s) and the panels (data-nav-scope="id"), so a menu in a
  // documentation example never touches the menu of the page. There can be two switchers in the scope (under the logo and at the foot).
  // Menus can have a parent in the popup (data-nav-group on its button, data-nav-parent on the menus below it): one level deep, the group
  // that has the chosen menu is open, and a closed parent carries a dot when a menu inside it (not the chosen one) asks for attention.
  function setNavGroup(btn, open) {
    var group = doc.getElementById(btn.getAttribute('aria-controls'));
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (group) {
      if (!open && group.contains(doc.activeElement)) { btn.focus({ preventScroll: true }); }
      group.hidden = !open;
    }
    // the popup changes its height: it is placed again, so that it never covers the switcher it opened from
    if (openMenu && openMenu.menu.contains(btn)) { placeMenu(openMenu.menu, openMenu.btn, openMenu.btn.getAttribute('data-menu-place')); }
  }

  function setNavSet(id, popup) {
    var menu = popup || $('.mat-menu[data-nav-scope]');
    var scope = menu && doc.getElementById(menu.getAttribute('data-nav-scope'));
    if (!scope) { return; }
    var panels = $$('[data-nav-panel]', scope);
    if (!panels.some(function (p) { return p.getAttribute('data-nav-panel') === id; })) { return; }
    panels.forEach(function (p) { p.hidden = p.getAttribute('data-nav-panel') !== id; });
    var chosen = null;
    var buttons = $$('[data-nav-set]', menu);
    buttons.forEach(function (b) {
      var on = b.getAttribute('data-nav-set') === id;
      b.setAttribute('aria-checked', on ? 'true' : 'false');
      if (on) { chosen = b; }
    });
    var others = function (b) { return b !== chosen && b.hasAttribute('data-nav-attention'); };
    $$('.mat-switcher', scope).forEach(function (switcher) {
      if (!chosen) { return; }
      var name = $('.mat-switcher__name', switcher);
      if (name) { name.textContent = chosen.getAttribute('data-nav-title') || ''; }
      var small = $('.mat-switcher__text small', switcher);
      if (small) { small.textContent = chosen.getAttribute('data-nav-parent') || small.getAttribute('data-nav-label') || small.textContent; }
      var use = $('.mat-switcher__icon use', switcher);
      var from = $('use', chosen);
      if (use && from) { use.setAttribute('href', from.getAttribute('href')); }
      var dot = $('.mat-switcher__dot', switcher);
      if (dot) { dot.hidden = !buttons.some(others); }
    });
    $$('[data-nav-group]', menu).forEach(function (btn) {
      var group = doc.getElementById(btn.getAttribute('aria-controls'));
      var inside = group ? $$('[data-nav-set]', group) : [];
      if (chosen && inside.indexOf(chosen) >= 0) { setNavGroup(btn, true); }
      var dot = $('.mat-nav__badge--sum', btn);
      if (dot) { dot.hidden = !inside.some(others); }
    });
    var shown = $('[data-nav-panel]:not([hidden])', scope);
    if (shown) { shown.scrollTop = 0; }
    root.dispatchEvent(new CustomEvent('mat:navset', { detail: { set: id } }));
  }

  // ------------------------------------------------------------------------------------------ on this page: scrollspy (SHELL-16)
  // An index (data-scrollspy) of links to sections of the page; the link of the section that is at the top of the screen is marked
  // (is-active, aria-current="location"). It works for several indexes at once (the one beside the content and the one in the dialog).
  var spyPending = false;

  function scrollContainer(el) {
    for (var p = el.parentElement; p; p = p.parentElement) {
      var o = getComputedStyle(p).overflowY;
      if ((o === 'auto' || o === 'scroll') && p.scrollHeight > p.clientHeight) { return p; }
    }
    return null;
  }

  function keepInView(box, el) {
    var b = box.getBoundingClientRect(), r = el.getBoundingClientRect();
    if (r.top < b.top) { box.scrollTop -= b.top - r.top + 8; } else if (r.bottom > b.bottom) { box.scrollTop += r.bottom - b.bottom + 8; }
  }

  function updateSpy() {
    spyPending = false;
    $$('[data-scrollspy]').forEach(function (nav) {
      var entries = [];
      $$('a[href^="#"]', nav).forEach(function (a) {
        var target = doc.getElementById(decodeURIComponent(a.getAttribute('href').slice(1)));
        if (target) { entries.push({ a: a, target: target }); }
      });
      if (entries.length === 0) { return; }
      var targets = entries.map(function (e) { return e.target; }).filter(function (t, i, all) { return all.indexOf(t) === i; });
      targets.sort(function (a, b) { return a.compareDocumentPosition(b) & 4 ? -1 : 1; });
      var box = scrollContainer(targets[0]);
      var top = box ? box.getBoundingClientRect().top : 0;
      var line = top + (parseFloat(getComputedStyle(root).getPropertyValue('--mat-topbar-h')) || 56) + 24;
      var current = targets[0];
      targets.forEach(function (t) { if (t.getBoundingClientRect().top <= line) { current = t; } });
      // at the very end of the page the last section is the current one even when it is too short to reach the line
      if (box && box.scrollTop + box.clientHeight >= box.scrollHeight - 2) { current = targets[targets.length - 1]; }
      // The index follows the page only when the section in view changes. Scrolling the index itself is a scroll event too: following on every
      // event would pull the marked link back into view and the index could not be scrolled at all.
      var changed = nav._spyCurrent !== current;
      nav._spyCurrent = current;
      entries.forEach(function (e) {
        var on = e.target === current;
        e.a.classList.toggle('is-active', on);
        if (on) { e.a.setAttribute('aria-current', 'location'); } else { e.a.removeAttribute('aria-current'); }
        if (on && changed) { var aside = e.a.closest('.mat-toc-aside'); if (aside) { keepInView(aside, e.a); } }
      });
    });
  }

  function scheduleSpy() {
    if (!spyPending) { spyPending = true; window.requestAnimationFrame(updateSpy); }
  }

  // Arrow keys inside the menu: up and down walk the visible items, right opens a parent (or goes to its first child), left closes it (or goes to the parent).
  function navKeys(e) {
    var panel = e.target.closest('.mat-nav__scroll');
    if (!panel || e.ctrlKey || e.metaKey || e.altKey) { return false; }
    var keys = ['ArrowDown', 'ArrowUp', 'Home', 'End', 'ArrowRight', 'ArrowLeft'];
    if (keys.indexOf(e.key) < 0) { return false; }
    var items = $$('.mat-nav__item', panel).filter(function (el) { return !el.closest('.mat-nav__branch:not(.is-open)') || !el.classList.contains('mat-nav__item--sub'); });
    var current = e.target.closest('.mat-nav__item');
    var i = items.indexOf(current);
    if (i < 0) { return false; }
    var target = null;
    if (e.key === 'ArrowDown') { target = items[Math.min(items.length - 1, i + 1)]; }
    else if (e.key === 'ArrowUp') { target = items[Math.max(0, i - 1)]; }
    else if (e.key === 'Home') { target = items[0]; }
    else if (e.key === 'End') { target = items[items.length - 1]; }
    else if (e.key === 'ArrowRight' && current.hasAttribute('data-nav-branch')) {
      if (current.getAttribute('aria-expanded') !== 'true') { setBranch(current, true, true); }
      else { target = $('.mat-nav__sub .mat-nav__item', current.closest('.mat-nav__branch')); }
    } else if (e.key === 'ArrowLeft') {
      if (current.hasAttribute('data-nav-branch') && current.getAttribute('aria-expanded') === 'true') { setBranch(current, false, true); }
      else if (current.classList.contains('mat-nav__item--sub')) { target = $('[data-nav-branch]', current.closest('.mat-nav__branch')); }
    } else { return false; }
    e.preventDefault();
    if (target) { target.focus(); }
    return true;
  }

  // ------------------------------------------------------------------------------------------------ context menus
  var openMenu = null;

  function closeMenu() {
    if (!openMenu) { return; }
    var m = openMenu;
    openMenu = null;
    m.menu.hidden = true;
    m.btn.setAttribute('aria-expanded', 'false');
    if (m.restoreFocus) { m.btn.focus({ preventScroll: true }); }
  }

  function placeMenu(menu, btn, place) {
    menu.style.left = '0px'; menu.style.top = '0px'; menu.style.bottom = 'auto'; menu.style.minWidth = '';
    var r = btn.getBoundingClientRect();
    var vw = window.innerWidth, vh = window.innerHeight;
    var w = Math.max(menu.offsetWidth, Math.min(r.width, 320)), h = menu.offsetHeight;
    var below = place === 'bottom' || (place !== 'top' && r.top < h + 16);
    var top = below ? Math.min(r.bottom + 6, vh - h - 8) : Math.max(8, r.top - h - 6);
    var left = Math.min(Math.max(8, r.left), vw - w - 8);
    menu.style.minWidth = Math.max(menu.offsetWidth, Math.min(r.width, 320)) + 'px';
    menu.style.left = left + 'px';
    menu.style.top = Math.max(8, top) + 'px';
    menu.style.transformOrigin = (below ? 'top' : 'bottom') + ' left';
  }

  function toggleMenu(btn) {
    var menu = doc.getElementById(btn.getAttribute('data-menu-toggle'));
    if (!menu) { return; }
    if (openMenu && openMenu.menu === menu) { closeMenu(); return; }
    closeMenu();
    menu.hidden = false;
    placeMenu(menu, btn, btn.getAttribute('data-menu-place'));
    btn.setAttribute('aria-expanded', 'true');
    openMenu = { menu: menu, btn: btn, restoreFocus: false };
    var first = $('.mat-menu__item', menu);
    if (first) { first.focus({ preventScroll: true }); openMenu.restoreFocus = true; }
  }

  // ---------------------------------------------------------------------------------------------------- dialogs
  var openDialogs = 0;

  function dialogEl(ref) { return typeof ref === 'string' ? doc.getElementById(ref) : ref; }

  function syncLock() { root.classList.toggle('mat-lock', openDialogs > 0 || !!(app() && app().classList.contains('is-nav-open')) || !!(pickerOpen && pickerOpen.full)); }

  function openDialog(ref, opener) {
    var d = dialogEl(ref);
    if (!d || d.open) { return d; }
    d._matOpener = opener || doc.activeElement;
    if (typeof d.showModal === 'function') { d.showModal(); } else { d.setAttribute('open', ''); }
    openDialogs += 1;
    syncLock();
    var target = $('[autofocus]', d) || $('input:not([type=hidden]):not([disabled]), select, textarea', d);
    if (target) { target.focus({ preventScroll: true }); if (target.select && target.type === 'search') { target.select(); } }
    d.dispatchEvent(new CustomEvent('mat:dialog-open', { bubbles: true }));
    return d;
  }

  function closeDialog(ref, result) {
    var d = dialogEl(ref);
    if (!d || !d.open) { return; }
    if (result !== undefined) { d.returnValue = result; }
    if (typeof d.close === 'function') { d.close(result); } else { d.removeAttribute('open'); d.dispatchEvent(new Event('close')); }
  }

  // every <dialog class="mat-dialog">: restore focus and the scroll lock when it closes, whoever closed it (button, Esc, script)
  doc.addEventListener('close', function (e) {
    var d = e.target;
    if (!d || !d.classList || !d.classList.contains('mat-dialog')) { return; }
    openDialogs = Math.max(0, openDialogs - 1);
    syncLock();
    var op = d._matOpener;
    d._matOpener = null;
    if (op && op.focus && doc.contains(op)) { op.focus({ preventScroll: true }); }
  }, true);

  // A click on the backdrop closes a dialog that is not a data-entry form; on a phone there is no backdrop.
  doc.addEventListener('click', function (e) {
    var d = e.target;
    if (d && d.classList && d.classList.contains('mat-dialog') && d.open && !d.hasAttribute('data-static')) {
      var r = d.getBoundingClientRect();
      if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) { closeDialog(d, 'cancel'); }
    }
  });

  function buildDialog(opts) {
    var id = nextId('mat-dlg');
    var d = doc.createElement('dialog');
    d.className = 'mat-dialog mat-dialog--sm';
    d.id = id;
    d.setAttribute('role', opts.role || 'dialog');
    d.setAttribute('aria-labelledby', id + '-t');
    d.setAttribute('aria-describedby', id + '-b');
    d.setAttribute('data-static', '');
    d.innerHTML =
      '<form method="dialog" class="mat-dialog__form">' +
      '<div class="mat-dialog__head">' + (opts.icon ? icon(opts.icon) : '') +
      '<h2 class="mat-dialog__title" id="' + id + '-t">' + esc(opts.title || '') + '</h2>' +
      '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-dialog__close" data-dialog-close aria-label="' + esc(label('data-dlg-close', 'Close')) + '">' + icon('x') + '</button></div>' +
      '<div class="mat-dialog__body" id="' + id + '-b">' + opts.body + '</div>' +
      '<div class="mat-dialog__foot"><div class="mat-btn-row mat-btn-row--plain">' + opts.buttons + '</div></div>' +
      '</form>';
    doc.body.appendChild(d);
    return d;
  }

  function show(opts) {
    return new Promise(function (resolve) {
      var d = buildDialog(opts);
      var done = false;
      function finish(value) {
        if (done) { return; }
        done = true;
        resolve(value);
      }
      d.addEventListener('close', function () {
        var v = d.returnValue;
        var out = opts.result ? opts.result(d, v) : v === 'ok';
        finish(out);
        setTimeout(function () { d.remove(); }, 0);
      });
      d.addEventListener('submit', function (ev) { ev.preventDefault(); closeDialog(d, 'ok'); });
      openDialog(d);
    });
  }

  function confirmDialog(message, options) {
    var o = options || {};
    var okText = o.ok || label('data-dlg-ok', 'OK');
    var cancelText = o.cancel || label('data-dlg-cancel', 'Cancel');
    // destructive: Cancel first, the destructive action at the end (positive -> negative); otherwise OK first.
    var buttons = o.danger
      ? '<button type="button" class="mat-btn mat-btn--secondary" data-dialog-close>' + esc(cancelText) + '</button><span class="mat-btn-row__spacer"></span>' +
        '<button type="submit" class="mat-btn mat-btn--danger mat-btn--solid" value="ok">' + esc(okText) + '</button>'
      : '<button type="submit" class="mat-btn mat-btn--primary" value="ok" autofocus>' + esc(okText) + '</button>' +
        '<button type="button" class="mat-btn mat-btn--secondary" data-dialog-close>' + esc(cancelText) + '</button>';
    return show({ title: o.title || label('data-dlg-confirm', 'Please confirm'), icon: o.danger ? 'alert' : 'help-circle', role: 'alertdialog', body: '<p>' + esc(message) + '</p>', buttons: buttons });
  }

  function alertDialog(message, options) {
    var o = options || {};
    return show({
      title: o.title || label('data-dlg-notice', 'Notice'), icon: o.icon || 'info', role: 'alertdialog',
      body: '<p>' + esc(message) + '</p>',
      buttons: '<button type="submit" class="mat-btn mat-btn--primary" value="ok" autofocus>' + esc(o.ok || label('data-dlg-ok', 'OK')) + '</button>',
      result: function () { return undefined; }
    });
  }

  function promptDialog(message, options) {
    var o = options || {};
    var id = nextId('mat-in');
    return show({
      title: o.title || label('data-dlg-input', 'Input'), icon: 'edit',
      body: '<div class="mat-field"><label class="mat-label" for="' + id + '">' + esc(message) + '</label>' +
            '<input class="mat-input" id="' + id + '" name="value" value="' + esc(o.value || '') + '" ' + (o.placeholder ? 'placeholder="' + esc(o.placeholder) + '"' : '') + ' autocomplete="off" autofocus></div>',
      buttons: '<button type="submit" class="mat-btn mat-btn--primary" value="ok">' + esc(o.ok || label('data-dlg-ok', 'OK')) + '</button>' +
               '<button type="button" class="mat-btn mat-btn--secondary" data-dialog-close>' + esc(o.cancel || label('data-dlg-cancel', 'Cancel')) + '</button>',
      result: function (d, v) { var i = $('input[name=value]', d); return v === 'ok' && i ? i.value : null; }
    });
  }

  // data-confirm="..." on a button, a link or a form
  var confirmed = new WeakSet();
  function guardConfirm(e, el, proceed) {
    if (confirmed.has(el)) { return; }
    var msg = el.getAttribute('data-confirm');
    if (!msg) { return; }
    e.preventDefault();
    e.stopPropagation();
    var danger = el.hasAttribute('data-confirm-danger') || el.classList.contains('mat-btn--danger');
    confirmDialog(msg, { danger: danger, ok: el.getAttribute('data-confirm-ok') || undefined, title: el.getAttribute('data-confirm-title') || undefined }).then(function (ok) {
      if (!ok) { return; }
      // proceed() dispatches synchronously, so the mark only has to live that long: the next activation asks again.
      confirmed.add(el);
      try { proceed(); } finally { confirmed.delete(el); }
    });
  }

  // ----------------------------------------------------------------------------------------------------- toasts
  function toast(message, options) {
    var o = options || {};
    var host = $('.mat-toasts');
    if (!host) { host = doc.createElement('div'); host.className = 'mat-toasts'; host.setAttribute('role', 'status'); host.setAttribute('aria-live', 'polite'); doc.body.appendChild(host); }
    var t = doc.createElement('div');
    t.className = 'mat-toast' + (o.tone ? ' mat-toast--' + o.tone : '');
    t.innerHTML = icon(o.tone === 'danger' ? 'x-circle' : o.tone === 'success' ? 'check-circle' : o.tone === 'warning' ? 'alert' : 'info') +
      '<div class="mat-alert__body">' + esc(message) + '</div>' +
      '<button type="button" class="mat-alert__close" aria-label="' + esc(label('data-dlg-close', 'Close')) + '">' + icon('x') + '</button>';
    host.appendChild(t);
    function remove() { if (t.parentNode) { t.parentNode.removeChild(t); } }
    $('.mat-alert__close', t).addEventListener('click', remove);
    setTimeout(remove, o.timeout || 6000);
  }

  // ----------------------------------------------------------------------------------------- dependent form fields
  function controllerValue(ctl) {
    if (!ctl) { return ''; }
    if (ctl.type === 'checkbox') { return ctl.checked ? 'true' : 'false'; }
    if (ctl.type === 'radio') { var on = $('input[name="' + ctl.name + '"]:checked'); return on ? on.value : ''; }
    return ctl.value;
  }

  function findController(spec, scope) {
    if (!spec) { return null; }
    if (spec.charAt(0) === '#' || spec.charAt(0) === '.' || spec.charAt(0) === '[') { return $(spec, scope) || $(spec); }
    return $('[name="' + spec + '"]', scope) || $('[name="' + spec + '"]');
  }

  function refreshDependents(scope) {
    $$('[data-show-if]', scope).forEach(function (el) {
      var ctl = findController(el.getAttribute('data-show-if'), el.closest('form') || undefined);
      if (!ctl) { return; }
      var value = controllerValue(ctl);
      var want = el.getAttribute('data-show-value');
      var show;
      if (want === null || want === '*') { show = value !== '' && value !== 'false'; } else { show = want.split(',').map(function (s) { return s.trim(); }).indexOf(value) !== -1; }
      el.hidden = !show;
      // a hidden field posts nothing and does not block the form
      $$('input, select, textarea', el).forEach(function (i) { i.disabled = !show; });
      $$('.mat-picker', el).forEach(syncPicker);
    });
  }

  // ---------------------------------------------------------------------------------------------------- validation
  // Browser bubbles are off (novalidate): the message goes to the reserved .mat-error line of the field.
  function fieldOf(input) { return input.closest('.mat-field'); }
  function showError(input, msg) {
    var f = fieldOf(input);
    if (!f) { return; }
    f.classList.toggle('is-invalid', !!msg);
    var e = $('.mat-error', f);
    if (e) { e.textContent = msg || ''; }
    input.setAttribute('aria-invalid', msg ? 'true' : 'false');
    if (input.classList.contains('mat-picker__input')) {
      var trigger = $('.mat-picker__trigger', f);
      if (trigger) { trigger.setAttribute('aria-invalid', msg ? 'true' : 'false'); }
      var entry = $('.mat-tagfield__input', f);
      if (entry) { entry.setAttribute('aria-invalid', msg ? 'true' : 'false'); }
    }
  }
  function messageFor(input) {
    var v = input.validity;
    if (v.valueMissing) { return input.getAttribute('data-msg-required') || label('data-msg-required', 'Please fill in this field.'); }
    if (v.typeMismatch) { return input.getAttribute('data-msg-type') || label('data-msg-type', 'Please enter a valid value.'); }
    if (v.patternMismatch) { return input.getAttribute('data-msg-pattern') || input.title || label('data-msg-pattern', 'The format is not valid.'); }
    if (v.tooShort || v.tooLong || v.rangeUnderflow || v.rangeOverflow) { return input.getAttribute('data-msg-range') || input.validationMessage; }
    return input.validationMessage;
  }
  function labelOf(input) {
    var l = input.id ? $('label[for="' + input.id.replace(/"/g, '\\"') + '"]', doc) : null;
    var f = fieldOf(input);
    var text = l ? l.textContent : (f && $('legend, .mat-label', f) ? $('legend, .mat-label', f).textContent : input.name);
    return (text || '').replace(/\*/g, '').trim();
  }
  // The summary at the top of a form (FORM-03): an alert with a link to every field that has an error. Server-rendered markup of the same shape works without the script.
  function summarize(form, bad) {
    var box = $('[data-validation-summary]', form);
    if (!box) { return; }
    var list = $('.mat-alert__list', box);
    if (list) {
      list.textContent = '';
      bad.forEach(function (i) {
        var li = doc.createElement('li');
        var a = doc.createElement('a');
        a.href = '#' + i.id;
        a.textContent = labelOf(i) + ': ' + messageFor(i);
        li.appendChild(a);
        list.appendChild(li);
      });
    }
    box.hidden = bad.length === 0;
  }
  doc.addEventListener('submit', function (e) {
    var form = e.target;
    if (!form.matches || !form.matches('form.mat-form, form[data-validate]') || e.defaultPrevented) { return; }
    var bad = $$('input, select, textarea', form).filter(function (i) { return !i.disabled && !i.checkValidity(); });
    $$('input, select, textarea', form).forEach(function (i) { if (!bad.includes(i)) { showError(i, ''); } });
    // a group of radio buttons has one message
    bad = bad.filter(function (i, n) { return i.type !== 'radio' || !i.name || bad.findIndex(function (o) { return o.type === 'radio' && o.name === i.name; }) === n; });
    summarize(form, bad);
    if (bad.length) {
      e.preventDefault();
      bad.forEach(function (i) { showError(i, messageFor(i)); });
      bad[0].focus();
    }
  }, true);
  // (typing in the text field of a tag input says nothing: the value is the real input's, and it changes when a tag is added)
  doc.addEventListener('input', function (e) { var i = e.target; if (i.closest && !i.classList.contains('mat-tagfield__input') && i.closest('.mat-field.is-invalid') && i.checkValidity()) { showError(i, ''); } });

  // ------------------------------------------------------------------------------------------------ form helpers (matui.forms.css)
  // range: the filled part of the track and an <output> (data-output="id"); counter: "12 / 80" (data-counter="id"); indeterminate checks;
  // password: the eye button (data-password-toggle="id"); dropzone: dragging files onto a label that holds a file input.
  function updateRange(input) {
    var min = parseFloat(input.min) || 0, max = isNaN(parseFloat(input.max)) ? 100 : parseFloat(input.max);
    input.style.setProperty('--mat-range', (max === min ? 0 : ((parseFloat(input.value) - min) / (max - min)) * 100) + '%');
    var out = input.getAttribute('data-output') && doc.getElementById(input.getAttribute('data-output'));
    if (out) { out.textContent = input.value; }
  }
  function updateCounter(input) {
    var out = doc.getElementById(input.getAttribute('data-counter'));
    if (out) { out.textContent = input.value.length + (input.maxLength > 0 ? ' / ' + input.maxLength : ''); }
  }
  function updateFiles(input) {
    var zone = input.closest('.mat-dropzone');
    var list = zone && $('.mat-dropzone__files', zone);
    if (list) { list.textContent = Array.prototype.map.call(input.files || [], function (f) { return f.name; }).join(', '); }
  }
  function initForms(scope) {
    $$('input[data-indeterminate]', scope).forEach(function (i) { i.indeterminate = true; });
    $$('input.mat-range', scope).forEach(updateRange);
    $$('[data-counter]', scope).forEach(updateCounter);
    $$('[data-selectable]', scope).forEach(updateSelection);
  }
  doc.addEventListener('input', function (e) {
    var t = e.target;
    if (t.matches && t.matches('input.mat-range')) { updateRange(t); }
    if (t.matches && t.matches('[data-counter]')) { updateCounter(t); }
  });
  doc.addEventListener('change', function (e) {
    var t = e.target;
    if (t.matches && t.matches('.mat-dropzone input[type=file]')) { updateFiles(t); }
  });
  ['dragenter', 'dragover'].forEach(function (name) {
    doc.addEventListener(name, function (e) { var z = e.target.closest && e.target.closest('.mat-dropzone'); if (z) { e.preventDefault(); z.classList.add('is-drag'); } });
  });
  ['dragleave', 'drop'].forEach(function (name) {
    doc.addEventListener(name, function (e) {
      var z = e.target.closest && e.target.closest('.mat-dropzone');
      if (!z) { return; }
      e.preventDefault();
      z.classList.remove('is-drag');
      if (name === 'drop' && e.dataTransfer && e.dataTransfer.files.length) {
        var input = $('input[type=file]', z);
        if (input) { input.files = e.dataTransfer.files; input.dispatchEvent(new Event('change', { bubbles: true })); }
      }
    });
  });

  // ------------------------------------------------------------------------------------------------ selectable rows of a table
  // An element with data-selectable holds a table with a checkbox per row (data-select-row), one in the header (data-select-all) and a bulk bar
  // ([data-bulk-bar] with [data-bulk-count]) that is shown only while rows are selected. The row of a selected check is marked (is-selected).
  function updateSelection(box) {
    var rows = $$('input[data-select-row]', box);
    var all = $('input[data-select-all]', box);
    var selected = rows.filter(function (r) { return r.checked; }).length;
    rows.forEach(function (r) { var tr = r.closest('tr'); if (tr) { tr.classList.toggle('is-selected', r.checked); } });
    if (all) { all.checked = rows.length > 0 && selected === rows.length; all.indeterminate = selected > 0 && selected < rows.length; }
    var bar = $('[data-bulk-bar]', box);
    if (bar) {
      bar.hidden = selected === 0;
      var count = $('[data-bulk-count]', bar);
      if (count) { count.textContent = String(selected); }
    }
  }
  doc.addEventListener('change', function (e) {
    var t = e.target;
    var box = t.closest && t.closest('[data-selectable]');
    if (!box || !t.matches) { return; }
    if (t.matches('input[data-select-all]')) {
      $$('input[data-select-row]', box).forEach(function (r) { r.checked = t.checked; });
      updateSelection(box);
    } else if (t.matches('input[data-select-row]')) {
      updateSelection(box);
    }
  });

  // ------------------------------------------------------------------------------------------------------- tabs
  function selectTab(tab, focus) {
    var group = tab.closest('[data-tabs]');
    if (!group) { return; }
    $$('[role=tab]', group).forEach(function (t) {
      var on = t === tab;
      t.setAttribute('aria-selected', on ? 'true' : 'false');
      t.tabIndex = on ? 0 : -1;
      var panel = doc.getElementById(t.getAttribute('aria-controls'));
      if (panel) { panel.hidden = !on; }
    });
    if (focus) { tab.focus(); }
  }

  // --------------------------------------------------------------------------------------------- list / gallery view
  function setView(btn) {
    var scope = btn.closest('[data-view-scope]') || doc;
    var view = btn.getAttribute('data-view-switch');
    $$('[data-view-switch]', scope).forEach(function (b) { b.setAttribute('aria-pressed', b === btn ? 'true' : 'false'); });
    $$('[data-view]', scope).forEach(function (v) { v.hidden = v.getAttribute('data-view') !== view; });
    var key = btn.getAttribute('data-view-key');
    if (key) { try { localStorage.setItem('mat-view-' + key, view); } catch (e) { /* ignore */ } }
  }

  // ---------------------------------------------------------------------------------------------------- clipboard
  function copy(text, el) {
    function done() { if (!el) { return; } el.classList.add('is-done'); var old = el.getAttribute('data-tip'); el.setAttribute('data-tip', label('data-copied', 'Copied')); setTimeout(function () { el.classList.remove('is-done'); if (old) { el.setAttribute('data-tip', old); } else { el.removeAttribute('data-tip'); } }, 1400); }
    if (navigator.clipboard && window.isSecureContext) { navigator.clipboard.writeText(text).then(done); return; }
    var ta = doc.createElement('textarea'); ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0'; doc.body.appendChild(ta); ta.select();
    try { doc.execCommand('copy'); done(); } catch (e) { /* ignore */ }
    ta.remove();
  }

  // ----------------------------------------------------------------------------------------- delegated click handling
  doc.addEventListener('click', function (e) {
    var t = e.target;
    if (!t.closest) { return; }

    var themeBtn = t.closest('[data-theme-toggle]');
    if (themeBtn) { cycleTheme(themeBtn); return; }
    var themeSet = t.closest('[data-theme-set]');
    if (themeSet) { chooseTheme(themeSet, themeSet.getAttribute('data-theme-set')); return; }

    var branchBtn = t.closest('[data-nav-branch]');
    if (branchBtn) { setBranch(branchBtn, branchBtn.getAttribute('aria-expanded') !== 'true', true); return; }
    // the parent of some menus in the popup of the switcher only opens or closes its menus: the popup stays
    var groupBtn = t.closest('[data-nav-group]');
    if (groupBtn) { setNavGroup(groupBtn, groupBtn.getAttribute('aria-expanded') !== 'true'); return; }

    var navToggle = t.closest('[data-nav-toggle]');
    if (navToggle) { setNav(!(app() && app().classList.contains('is-nav-open')), navToggle); return; }
    // a link in the drawer closes it; a parent of a submenu only opens its children
    if (t.closest('[data-nav-close]') || t.classList.contains('mat-backdrop') || (t.closest('.mat-nav__item:not([data-nav-branch])') && app() && app().classList.contains('is-nav-open'))) { setNav(false); }

    var menuBtn = t.closest('[data-menu-toggle]');
    if (menuBtn) { e.preventDefault(); toggleMenu(menuBtn); return; }
    if (openMenu && !t.closest('.mat-menu')) { closeMenu(); }
    else if (openMenu && t.closest('.mat-menu__item')) { setTimeout(closeMenu, 0); }

    var setBtn = t.closest('[data-nav-set]');
    if (setBtn) { setNavSet(setBtn.getAttribute('data-nav-set'), setBtn.closest('.mat-menu')); return; }

    // a link to a section of the page inside a dialog (the index on a phone) closes the dialog and lets the browser jump
    var jump = t.closest('dialog a[href^="#"]');
    if (jump) { closeDialog(jump.closest('dialog'), 'jump'); setTimeout(scheduleSpy, 50); return; }

    var opener = t.closest('[data-dialog-open]');
    if (opener) { e.preventDefault(); openDialog(opener.getAttribute('data-dialog-open'), opener); return; }
    var closer = t.closest('[data-dialog-close]');
    if (closer) { var d = closer.closest('dialog'); if (d) { e.preventDefault(); closeDialog(d, 'cancel'); } return; }

    var cf = t.closest('[data-confirm]');
    if (cf && !cf.matches('form')) {
      guardConfirm(e, cf, function () {
        if (cf.tagName === 'A') { window.location.href = cf.href; }
        else if (cf.form && cf.type === 'submit') { cf.form.requestSubmit(cf); }
        else { cf.click(); }
      });
      if (e.defaultPrevented) { return; }
    }

    var pw = t.closest('[data-password-toggle]');
    if (pw) {
      var field = doc.getElementById(pw.getAttribute('data-password-toggle'));
      if (field) {
        var reveal = field.type === 'password';
        field.type = reveal ? 'text' : 'password';
        pw.setAttribute('aria-pressed', reveal ? 'true' : 'false');
      }
      return;
    }

    // a link of the summary of a form moves the focus to the field instead of only scrolling
    var summaryLink = t.closest('[data-validation-summary] a[href^="#"]');
    if (summaryLink) {
      var target = doc.getElementById(summaryLink.getAttribute('href').slice(1));
      if (target) { e.preventDefault(); target.focus(); }
      return;
    }

    var cp = t.closest('[data-copy]');
    if (cp) {
      var spec = cp.getAttribute('data-copy');
      var src = spec.charAt(0) === '#' ? $(spec) : null;
      copy(src ? (src.value !== undefined && src.tagName !== 'DIV' ? src.value : src.textContent.trim()) : spec, cp);
      return;
    }

    var tab = t.closest('[role=tab]');
    if (tab && tab.closest('[data-tabs]')) { selectTab(tab); return; }

    var dismiss = t.closest('[data-dismiss]');
    if (dismiss) { var al = dismiss.closest('.mat-alert, .mat-toast'); if (al) { al.remove(); } return; }

    var view = t.closest('[data-view-switch]');
    if (view) { setView(view); }
  });

  doc.addEventListener('submit', function (e) {
    var form = e.target;
    if (form.matches && form.matches('form[data-confirm]')) { guardConfirm(e, form, function () { form.requestSubmit(); }); }
  }, true);

  doc.addEventListener('change', function (e) {
    if (e.target.closest && e.target.closest('form, body')) { refreshDependents(e.target.closest('form') || undefined); }
  });

  // ---------------------------------------------------------------------------------------------------- keyboard
  doc.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
      if (openMenu) { openMenu.restoreFocus = true; closeMenu(); return; }
      var shell = app();
      if (shell && shell.classList.contains('is-nav-open')) { setNav(false); }
      return;
    }

    if (openMenu && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
      // the menus of a closed parent are not walked
      var items = $$('.mat-menu__item', openMenu.menu).filter(function (el) { return !el.closest('[hidden]'); });
      var i = items.indexOf(doc.activeElement);
      e.preventDefault();
      items[(i + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length].focus();
      return;
    }

    // in the popup of the switcher: right opens a parent (or goes to its first menu), left closes it (or goes back to the parent)
    if (openMenu && (e.key === 'ArrowRight' || e.key === 'ArrowLeft')) {
      var cur = doc.activeElement;
      var inGroup = cur && cur.closest ? cur.closest('.mat-menu__sub') : null;
      if (cur && cur.hasAttribute && cur.hasAttribute('data-nav-group')) {
        var isOpen = cur.getAttribute('aria-expanded') === 'true';
        if (e.key === 'ArrowRight') {
          e.preventDefault();
          if (!isOpen) { setNavGroup(cur, true); } else { var firstChild = $('.mat-menu__item', doc.getElementById(cur.getAttribute('aria-controls'))); if (firstChild) { firstChild.focus(); } }
          return;
        }
        if (isOpen) { e.preventDefault(); setNavGroup(cur, false); return; }
      } else if (e.key === 'ArrowLeft' && inGroup) {
        var parent = $('[data-nav-group][aria-controls="' + inGroup.id + '"]', openMenu.menu);
        if (parent) { e.preventDefault(); setNavGroup(parent, false); parent.focus(); return; }
      }
    }

    if (!openMenu && e.target.closest && navKeys(e)) { return; }

    var tab = e.target.closest && e.target.closest('[role=tab]');
    if (tab && (e.key === 'ArrowRight' || e.key === 'ArrowLeft' || e.key === 'Home' || e.key === 'End')) {
      var tabs = $$('[role=tab]', tab.closest('[data-tabs]'));
      var idx = tabs.indexOf(tab);
      var to = e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : (idx + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
      e.preventDefault();
      selectTab(tabs[to], true);
      return;
    }

    // shortcuts such as "/" for the search dialog; never while typing
    var tag = (e.target.tagName || '').toLowerCase();
    if (e.ctrlKey || e.metaKey || e.altKey || tag === 'input' || tag === 'textarea' || tag === 'select' || e.target.isContentEditable) { return; }
    var sc = $$('[data-shortcut]').filter(function (el) { return el.getAttribute('data-shortcut') === e.key; })[0];
    if (sc && !sc.disabled) {
      e.preventDefault();
      if (sc.matches('input, textarea')) { sc.focus(); sc.select(); } else { sc.click(); }
    }
  });

  window.addEventListener('resize', function () { closeMenu(); scheduleSpy(); });
  doc.addEventListener('scroll', function (e) { if (openMenu && !e.target.closest) { closeMenu(); } scheduleSpy(); }, true);
  window.addEventListener('hashchange', scheduleSpy);

  // ------------------------------------------------------------------------------------------------ pickers (FORM-15, matui.pickers.css)
  // A picker is a field that is filled in a panel. The markup is a wrapper (data-picker="select|date|daterange|time|timerange|colour|tags") around the real
  // input (.mat-picker__input), which holds the value that is posted, always in the same format whatever the language of the page:
  //   date 2026-10-10 · daterange 2026-10-10..2026-10-14 (and ~2 for "plus or minus two days") · time 09:30 · timerange 09:00..17:30 · colour #b4235f
  //   select: the data-value of the option; with data-multiple the values joined by data-sep (default ", ") · tags: the tags joined by data-sep (default ", ")
  // The script adds the button that shows the value (the label of the field names it) and the panel. On a phone the panel is full screen with an X.
  var PK_TEXT = {
    en: {
      apply: 'Apply', cancel: 'Cancel', clear: 'Clear', close: 'Close', done: 'Done', today: 'Today', now: 'Now',
      prevMonth: 'Previous month', nextMonth: 'Next month',
      phSelect: 'Choose', phDate: 'Choose a date', phPeriod: 'Choose a period', phTime: 'Choose a time', phTimeRange: 'Choose a time range', phColour: 'Choose a colour',
      search: 'Search', noResults: 'Nothing found', selectedCount: '{0} selected',
      startDate: 'Choose the start date', endDate: 'Choose the end date', daysCount: '{0} days', oneDay: '1 day',
      exactDates: 'Exact dates', flexible: 'Flexible dates', flexDays: '± {0} days', flexDay: '± 1 day',
      from: 'From', to: 'To', hour: 'Hour', minute: 'Minute', ampm: 'AM or PM', typeTime: 'Type a time', nextDay: 'next day', durHM: '{0} h {1} min', durH: '{0} h', durM: '{0} min',
      colours: 'Colours', hue: 'Hue', area: 'Saturation and brightness', areaValue: 'Saturation {0}%, brightness {1}%', hex: 'Hex colour',
      whiteText: 'White text', darkText: 'Dark text', lvAAA: 'AAA', lvAA: 'AA', lvLarge: 'large text only', lvLow: 'too low',
      phTags: 'Add a tag', removeTag: 'Remove tag {0}', tagRemoved: 'Removed {0}', tagDuplicate: '{0} is already there', tagMax: 'At most {0} tags', tagStrict: 'Choose one of the suggestions', suggestions: 'Suggestions'
    },
    de: {
      apply: 'Übernehmen', cancel: 'Abbrechen', clear: 'Löschen', close: 'Schließen', done: 'Fertig', today: 'Heute', now: 'Jetzt',
      prevMonth: 'Vorheriger Monat', nextMonth: 'Nächster Monat',
      phSelect: 'Auswählen', phDate: 'Datum wählen', phPeriod: 'Zeitraum wählen', phTime: 'Uhrzeit wählen', phTimeRange: 'Zeitbereich wählen', phColour: 'Farbe wählen',
      search: 'Suchen', noResults: 'Nichts gefunden', selectedCount: '{0} gewählt',
      startDate: 'Startdatum wählen', endDate: 'Enddatum wählen', daysCount: '{0} Tage', oneDay: '1 Tag',
      exactDates: 'Genaue Daten', flexible: 'Flexible Daten', flexDays: '± {0} Tage', flexDay: '± 1 Tag',
      from: 'Von', to: 'Bis', hour: 'Stunde', minute: 'Minute', ampm: 'Vor- oder nachmittags', typeTime: 'Uhrzeit eintippen', nextDay: 'am nächsten Tag', durHM: '{0} Std. {1} Min.', durH: '{0} Std.', durM: '{0} Min.',
      colours: 'Farben', hue: 'Farbton', area: 'Sättigung und Helligkeit', areaValue: 'Sättigung {0} %, Helligkeit {1} %', hex: 'Hex-Farbe',
      whiteText: 'Weiße Schrift', darkText: 'Dunkle Schrift', lvAAA: 'AAA', lvAA: 'AA', lvLarge: 'nur große Schrift', lvLow: 'zu niedrig',
      phTags: 'Tag hinzufügen', removeTag: 'Tag {0} entfernen', tagRemoved: '{0} entfernt', tagDuplicate: '{0} ist schon vorhanden', tagMax: 'Höchstens {0} Tags', tagStrict: 'Bitte einen Vorschlag wählen', suggestions: 'Vorschläge'
    }
  };
  var PK_SWATCHES = ['#dc2626', '#ea580c', '#d97706', '#ca8a04', '#65a30d', '#16a34a', '#0d9488', '#0891b2', '#0284c7', '#2563eb', '#4f46e5', '#7c3aed', '#a21caf', '#db2777', '#e11d48', '#475569', '#1f2937', '#78716c'];
  var PK_INK = '#0b1020';
  var KINDS = {};
  var pickerOpen = null;
  var pkLocaleName = null;
  var pkFormats = null;

  function pkText(key, a, b) {
    var table = PK_TEXT[(root.lang || 'en').slice(0, 2).toLowerCase()] || PK_TEXT.en;
    var s = root.getAttribute('data-pk-' + key.toLowerCase()) || table[key] || PK_TEXT.en[key] || key;
    // function replacers: what a person typed (a tag) may hold a "$&" that a plain replacement string would expand
    return s.replace('{0}', function () { return a === undefined ? '' : a; }).replace('{1}', function () { return b === undefined ? '' : b; });
  }
  function pkLocale() {
    if (!pkLocaleName) {
      var l = root.getAttribute('data-locale') || root.lang || navigator.language || 'en-GB';
      if (/^en$/i.test(l)) { l = 'en-GB'; }
      try { Intl.DateTimeFormat(l); } catch (e) { l = 'en-GB'; }
      pkLocaleName = l;
    }
    return pkLocaleName;
  }
  function formats() {
    if (!pkFormats) {
      var l = pkLocale();
      pkFormats = {
        day: new Intl.DateTimeFormat(l, { dateStyle: 'medium' }),
        full: new Intl.DateTimeFormat(l, { dateStyle: 'full' }),
        month: new Intl.DateTimeFormat(l, { month: 'long', year: 'numeric' }),
        dow: new Intl.DateTimeFormat(l, { weekday: 'short' }),
        dowLong: new Intl.DateTimeFormat(l, { weekday: 'long' })
      };
    }
    return pkFormats;
  }
  function isPhone() { return window.matchMedia('(max-width: 640px)').matches; }
  function clamp(n, lo, hi) { return Math.min(hi, Math.max(lo, n)); }
  function p2(n) { return (n < 10 ? '0' : '') + n; }

  // ---- dates
  function isoOf(d) { return d.getFullYear() + '-' + p2(d.getMonth() + 1) + '-' + p2(d.getDate()); }
  function dateOf(s) {
    var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s || '');
    if (!m) { return null; }
    var d = new Date(+m[1], +m[2] - 1, +m[3]);
    return d.getFullYear() === +m[1] && d.getMonth() === +m[2] - 1 && d.getDate() === +m[3] ? d : null;
  }
  function dayStart(d) { return new Date(d.getFullYear(), d.getMonth(), d.getDate()); }
  function firstOf(d) { return new Date(d.getFullYear(), d.getMonth(), 1); }
  function addDays(d, n) { return new Date(d.getFullYear(), d.getMonth(), d.getDate() + n); }
  function addMonths(d, n) {
    var x = new Date(d.getFullYear(), d.getMonth() + n, 1);
    x.setDate(Math.min(d.getDate(), new Date(x.getFullYear(), x.getMonth() + 1, 0).getDate()));
    return x;
  }
  function limitDate(d, min, max) { return min && d < min ? min : max && d > max ? max : d; }

  // ---- times
  function timeOf(s) {
    var m = /^(\d{1,2}):(\d{2})$/.exec((s || '').trim());
    if (!m || +m[1] > 23 || +m[2] > 59) { return null; }
    return { h: +m[1], m: +m[2] };
  }
  function timeStr(t) { return p2(t.h) + ':' + p2(t.m); }
  // What a person types: 9, 930, 0930, 9:30, 9.30, 9:30 pm, 930pm
  function typedTime(text) {
    var s = (text || '').toLowerCase().replace(/\s+/g, '');
    var mer = /([ap])\.?m?\.?$/.exec(s);
    if (mer) { s = s.slice(0, mer.index); }
    var h, n;
    var parts = s.split(/[:.]/);
    if (parts.length === 2) { h = parts[0]; n = parts[1]; }
    else if (/^\d{1,2}$/.test(s)) { h = s; n = '0'; }
    else if (/^\d{3,4}$/.test(s)) { h = s.slice(0, s.length - 2); n = s.slice(-2); }
    else { return null; }
    if (!/^\d{1,2}$/.test(h) || !/^\d{1,2}$/.test(n)) { return null; }
    h = +h; n = +n;
    if (mer) { if (h < 1 || h > 12) { return null; } h = h % 12 + (mer[1] === 'p' ? 12 : 0); }
    return h < 24 && n < 60 ? { h: h, m: n } : null;
  }
  function timeText(p, t) {
    return new Intl.DateTimeFormat(pkLocale(), { hour: '2-digit', minute: '2-digit', hourCycle: p.hour12 ? 'h12' : 'h23' }).format(new Date(2000, 0, 1, t.h, t.m));
  }

  // ---- colours
  function normHex(s) {
    var m = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i.exec((s || '').trim());
    if (!m) { return null; }
    var h = m[1].toLowerCase();
    if (h.length === 3) { h = h.charAt(0) + h.charAt(0) + h.charAt(1) + h.charAt(1) + h.charAt(2) + h.charAt(2); }
    return '#' + h;
  }
  function hsvToHex(h, s, v) {
    var c = v * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = v - c, r = 0, g = 0, b = 0;
    if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; } else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
    return '#' + [r, g, b].map(function (n) { var k = Math.round((n + m) * 255); return (k < 16 ? '0' : '') + k.toString(16); }).join('');
  }
  function hexToHsv(hex) {
    var c = [1, 3, 5].map(function (i) { return parseInt(hex.substr(i, 2), 16) / 255; });
    var max = Math.max(c[0], c[1], c[2]), min = Math.min(c[0], c[1], c[2]), d = max - min, h = 0;
    if (d) {
      if (max === c[0]) { h = 60 * (((c[1] - c[2]) / d) % 6); } else if (max === c[1]) { h = 60 * ((c[2] - c[0]) / d + 2); } else { h = 60 * ((c[0] - c[1]) / d + 4); }
    }
    return { h: h < 0 ? h + 360 : h, s: max ? d / max : 0, v: max };
  }
  // The text colour with the better contrast on this colour, and how good it is.
  function textOn(hex) {
    var l = luminance(hex);
    var white = 1.05 / (l + 0.05), dark = (l + 0.05) / (luminance(PK_INK) + 0.05);
    return white >= dark ? { name: 'whiteText', ratio: white, color: '#ffffff' } : { name: 'darkText', ratio: dark, color: PK_INK };
  }
  function levelOf(ratio) { return ratio >= 7 ? 'lvAAA' : ratio >= 4.5 ? 'lvAA' : ratio >= 3 ? 'lvLarge' : 'lvLow'; }

  // ---- the shared parts: the button, the panel, opening and closing
  function placeholderOf(p) {
    var own = p.wrap.getAttribute('data-placeholder');
    if (own) { return own; }
    return pkText({ select: 'phSelect', date: 'phDate', daterange: 'phPeriod', time: 'phTime', timerange: 'phTimeRange', colour: 'phColour', tags: 'phTags' }[p.kind]);
  }

  function pkPanel(p) {
    if (p.panel) { return p.panel; }
    var id = nextId('mat-pk');
    var panel = doc.createElement('div');
    panel.className = 'mat-picker__panel';
    panel.id = id;
    panel.hidden = true;
    panel.innerHTML = '<div class="mat-picker__head"><span class="mat-picker__heading" id="' + id + '-h"></span>' +
      '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon" data-picker-close aria-label="' + esc(pkText('close')) + '">' + icon('x') + '</button></div>' +
      '<div class="mat-picker__body"></div><div class="mat-picker__foot" hidden></div>';
    if (p.kind !== 'select') { panel.setAttribute('role', 'dialog'); panel.setAttribute('aria-labelledby', id + '-h'); }
    p.wrap.appendChild(panel);
    p.panel = panel;
    p.heading = $('.mat-picker__heading', panel);
    p.body = $('.mat-picker__body', panel);
    p.foot = $('.mat-picker__foot', panel);
    p.heading.textContent = labelOf(p.input) || placeholderOf(p);
    return panel;
  }

  // "apply": Apply and Cancel, "done": Done, nothing: no foot. The buttons are under the content, positive first.
  function setFoot(p, kind) {
    p.foot.innerHTML = kind === 'apply'
      ? '<button type="button" class="mat-btn mat-btn--primary" data-picker-action="apply">' + esc(pkText('apply')) + '</button>' +
        '<button type="button" class="mat-btn mat-btn--secondary" data-picker-action="cancel">' + esc(pkText('cancel')) + '</button>'
      : kind === 'done' ? '<button type="button" class="mat-btn mat-btn--primary" data-picker-action="cancel">' + esc(pkText('done')) + '</button>' : '';
    p.foot.hidden = !kind;
  }
  function refreshFoot(p) {
    var apply = $('[data-picker-action="apply"]', p.foot);
    if (apply) { apply.disabled = !p.spec.ready(p); }
  }

  function syncPicker(wrap) {
    var p = wrap._matPicker;
    if (!p) { return; }
    // a picker that is a field of its own (the tags) has no button and no panel that is opened: it draws itself from the value
    if (p.spec.inline) { p.spec.sync(p); return; }
    p.trigger.disabled = p.input.disabled;
    var out = $('.mat-picker__value', p.trigger);
    out.textContent = '';
    p.spec.display(p, out);
    var has = p.input.value !== '';
    out.classList.toggle('is-placeholder', !has);
    if (p.clear) { p.clear.hidden = !has || p.input.disabled; }
  }

  function setPickerValue(p, value) {
    if (p.input.value === value) { return; }
    p.input.value = value;
    syncPicker(p.wrap);
    p.input.dispatchEvent(new Event('input', { bubbles: true }));
    p.input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  function placePanel(p) {
    var panel = p.panel;
    p.full = isPhone();
    panel.style.left = ''; panel.style.top = ''; panel.style.minWidth = ''; panel.style.maxHeight = '';
    if (p.full) { return; }
    var r = p.trigger.getBoundingClientRect();
    var vw = window.innerWidth, vh = window.innerHeight;
    if (p.kind === 'select') { panel.style.minWidth = Math.max(280, r.width) + 'px'; }
    panel.style.left = '0px'; panel.style.top = '0px'; panel.style.maxHeight = (vh - 16) + 'px';
    var w = panel.offsetWidth, h = panel.offsetHeight;
    var below = vh - r.bottom - 10, above = r.top - 10;
    // below the field if it fits, else above it; when neither side has room the panel covers the field and stays inside the window
    var top, origin = 'top left';
    if (h <= below) { top = r.bottom + 6; }
    else if (h <= above) { top = r.top - h - 6; origin = 'bottom left'; }
    else { top = vh - h - 8; origin = 'center left'; }
    panel.style.transformOrigin = origin;
    panel.style.left = Math.max(8, Math.min(r.left, vw - w - 8)) + 'px';
    panel.style.top = Math.max(8, top) + 'px';
  }

  function openPicker(wrap) {
    var p = wrap._matPicker;
    if (!p || p.trigger.disabled) { return; }
    if (pickerOpen === p) { closePicker(true); return; }
    closePicker(false);
    closeMenu();
    pkPanel(p);
    p.full = isPhone();
    p.relayout = function () { if (pickerOpen === p) { placePanel(p); } };
    var focusEl = p.spec.open(p);
    p.panel.hidden = false;
    p.trigger.setAttribute('aria-expanded', 'true');
    pickerOpen = p;
    placePanel(p);
    syncLock();
    if (p.spec.shown) { p.spec.shown(p); }
    if (focusEl) {
      focusEl.focus({ preventScroll: true });
      // a field to type in starts selected, so that typing replaces what is there
      if (focusEl.select && /^(text|search)$/.test(focusEl.type || '')) { focusEl.select(); }
    }
  }

  function closePicker(restoreFocus) {
    var p = pickerOpen;
    if (!p) { return; }
    pickerOpen = null;
    p.panel.hidden = true;
    p.trigger.setAttribute('aria-expanded', 'false');
    p.state = null;
    syncLock();
    if (restoreFocus) { p.trigger.focus({ preventScroll: true }); }
  }

  function panelAction(p, action) {
    if (action === 'apply') {
      var v = p.spec.ready(p) ? p.spec.value(p) : null;
      if (v === null) { return; }
      setPickerValue(p, v);
    }
    closePicker(true);
  }

  function buildPicker(wrap) {
    if (wrap._matPicker) { return; }
    var kind = wrap.getAttribute('data-picker');
    var spec = KINDS[kind];
    var input = $('.mat-picker__input', wrap);
    if (!spec || !input) { return; }
    var p = { wrap: wrap, input: input, kind: kind, spec: spec, state: null };
    wrap._matPicker = p;
    if (!input.id) { input.id = nextId('mat-pk-in'); }
    input.tabIndex = -1;
    input.setAttribute('aria-hidden', 'true');
    input.setAttribute('autocomplete', 'off');
    if (spec.inline) {
      spec.init(p);
      syncPicker(wrap);
      return;
    }

    var trigger = doc.createElement('button');
    trigger.type = 'button';
    trigger.className = 'mat-input mat-picker__trigger';
    trigger.id = input.id + '-trigger';
    trigger.setAttribute('aria-haspopup', kind === 'select' ? 'listbox' : 'dialog');
    trigger.setAttribute('aria-expanded', 'false');
    trigger.innerHTML = '<span class="mat-picker__value" id="' + trigger.id + '-value"></span>' + icon(spec.icon);
    wrap.insertBefore(trigger, input.nextSibling);
    p.trigger = trigger;
    var lab = $('label[for="' + input.id + '"]');
    if (lab) {
      if (!lab.id) { lab.id = input.id + '-label'; }
      trigger.setAttribute('aria-labelledby', lab.id + ' ' + trigger.id + '-value');
    }
    var desc = input.getAttribute('aria-describedby');
    if (desc) { trigger.setAttribute('aria-describedby', desc); }

    if (wrap.hasAttribute('data-clearable')) {
      var clear = doc.createElement('button');
      clear.type = 'button';
      clear.className = 'mat-picker__clear';
      clear.setAttribute('aria-label', pkText('clear'));
      clear.innerHTML = icon('x');
      wrap.insertBefore(clear, trigger.nextSibling);
      p.clear = clear;
      trigger.classList.add('is-clearable');
    }
    spec.init(p);
    syncPicker(wrap);
    // leaving the picker with the Tab key closes it (the panel is not in the order of the page)
    wrap.addEventListener('focusout', function (e) {
      if (pickerOpen === p && e.relatedTarget && !wrap.contains(e.relatedTarget)) { closePicker(false); }
    });
  }

  function initPickers(scope) {
    $$('.mat-picker[data-picker]', scope).forEach(buildPicker);
    $$('.mat-picker', scope).forEach(syncPicker);
  }

  // ---- select: an option has a picture, a title, a description and tags
  function selValues(p) {
    var v = p.input.value;
    if (!p.multiple) { return v === '' ? [] : [v]; }
    return v.split(p.sep.trim() || p.sep).map(function (s) { return s.trim(); }).filter(Boolean);
  }
  function optionName(o) {
    var n = $('.mat-picker__name', o);
    return (n ? n.textContent : o.textContent).trim();
  }
  function focusOption(p, o) {
    $$('.mat-picker__option', p.list).forEach(function (x) { x.tabIndex = -1; });
    o.tabIndex = 0;
    o.focus({ preventScroll: true });
    keepInView(p.list, o);
  }
  function visibleOptions(p) { return $$('.mat-picker__option:not([hidden]):not(:disabled)', p.list); }
  function filterOptions(p, query) {
    var q = query.trim().toLowerCase(), shown = 0;
    $$('.mat-picker__option', p.list).forEach(function (o) {
      var hit = !q || (o.textContent + ' ' + (o.getAttribute('data-keywords') || '')).toLowerCase().indexOf(q) >= 0;
      o.hidden = !hit;
      if (hit) { shown += 1; }
    });
    p.empty.hidden = shown > 0;
    p.relayout();
  }

  KINDS.select = {
    icon: 'chevron-down',
    init: function (p) {
      p.multiple = p.wrap.hasAttribute('data-multiple');
      p.sep = p.wrap.hasAttribute('data-sep') ? p.wrap.getAttribute('data-sep') : ', ';
      p.list = $('.mat-picker__options', p.wrap) || doc.createElement('div');
      var panel = pkPanel(p);
      panel.classList.add('mat-picker__panel--list');
      p.list.setAttribute('role', 'listbox');
      p.list.setAttribute('aria-label', labelOf(p.input) || placeholderOf(p));
      if (p.multiple) { p.list.setAttribute('aria-multiselectable', 'true'); p.wrap.classList.add('mat-picker--multiple'); }
      $$('.mat-picker__option', p.list).forEach(function (o) {
        o.type = 'button';
        o.setAttribute('role', 'option');
        o.setAttribute('aria-selected', 'false');
        o.tabIndex = -1;
        if (o.disabled) { o.setAttribute('aria-disabled', 'true'); }
        if (!$('.mat-picker__check', o)) { o.insertAdjacentHTML('beforeend', '<span class="mat-picker__check" aria-hidden="true">' + icon('check') + '</span>'); }
      });
      if (p.wrap.hasAttribute('data-search')) {
        var search = doc.createElement('div');
        search.className = 'mat-picker__search';
        search.innerHTML = icon('search') + '<input type="search" class="mat-input" autocomplete="off" aria-label="' + esc(pkText('search')) + '" placeholder="' + esc(pkText('search')) + '">';
        p.body.appendChild(search);
        p.search = $('input', search);
      }
      p.list.hidden = false;
      p.body.appendChild(p.list);
      p.empty = doc.createElement('p');
      p.empty.className = 'mat-picker__empty';
      p.empty.setAttribute('role', 'status');
      p.empty.hidden = true;
      p.empty.textContent = pkText('noResults');
      p.body.appendChild(p.empty);
      setFoot(p, p.multiple ? 'done' : null);
    },
    display: function (p, out) {
      var vals = selValues(p);
      var chosen = [];
      $$('.mat-picker__option', p.list).forEach(function (o) {
        var on = vals.indexOf(o.getAttribute('data-value') || '') >= 0;
        o.setAttribute('aria-selected', on ? 'true' : 'false');
        if (on) { chosen.push(o); }
      });
      if (chosen.length === 0) { out.textContent = vals.length ? vals.join(p.sep) : placeholderOf(p); return; }
      if (p.multiple) {
        var names = chosen.map(optionName);
        out.textContent = chosen.length <= 2 ? names.join(', ') : pkText('selectedCount', chosen.length);
        return;
      }
      var img = $('.mat-picker__img', chosen[0]);
      if (img) { out.appendChild(img.cloneNode(true)); }
      var name = doc.createElement('span');
      name.textContent = optionName(chosen[0]);
      out.appendChild(name);
    },
    open: function (p) {
      if (p.search) { p.search.value = ''; filterOptions(p, ''); }
      var chosen = $('.mat-picker__option[aria-selected="true"]:not([hidden])', p.list);
      var first = chosen || visibleOptions(p)[0];
      if (first) { $$('.mat-picker__option', p.list).forEach(function (x) { x.tabIndex = -1; }); first.tabIndex = 0; }
      if (p.search && !p.full) { return p.search; }
      return first || p.search;
    },
    click: function (p, t) {
      var o = t.closest('.mat-picker__option');
      if (!o || o.disabled) { return; }
      var v = o.getAttribute('data-value') || '';
      if (!p.multiple) { setPickerValue(p, v); closePicker(true); return; }
      var vals = selValues(p), i = vals.indexOf(v);
      if (i >= 0) { vals.splice(i, 1); } else { vals.push(v); }
      var order = $$('.mat-picker__option', p.list).map(function (x) { return x.getAttribute('data-value') || ''; });
      vals.sort(function (a, b) { return order.indexOf(a) - order.indexOf(b); });
      setPickerValue(p, vals.join(p.sep));
      focusOption(p, o);
    },
    input: function (p, t) { if (t === p.search) { filterOptions(p, t.value); } },
    keys: function (p, e) {
      var t = e.target, opts = visibleOptions(p), o = t.closest('.mat-picker__option'), i = opts.indexOf(o), to = null;
      if (e.key === 'ArrowDown') { to = opts[Math.min(opts.length - 1, i + 1)]; }
      else if (e.key === 'ArrowUp') {
        if (i <= 0 && p.search) { e.preventDefault(); p.search.focus(); return; }
        to = opts[Math.max(0, i - 1)];
      }
      else if (e.key === 'Home' && o) { to = opts[0]; }
      else if (e.key === 'End' && o) { to = opts[opts.length - 1]; }
      else if (e.key === 'Enter' && t === p.search) { e.preventDefault(); if (opts[0]) { p.spec.click(p, opts[0]); } return; }
      else if (o && !p.search && e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
        // type the first letters of a title to jump to it
        p.typed = (p.typed || '') + e.key.toLowerCase();
        clearTimeout(p.typedTimer);
        p.typedTimer = setTimeout(function () { p.typed = ''; }, 700);
        var hit = opts.slice(i + 1).concat(opts.slice(0, i + 1)).filter(function (x) { return optionName(x).toLowerCase().indexOf(p.typed) === 0; })[0];
        if (hit) { e.preventDefault(); focusOption(p, hit); }
        return;
      } else { return; }
      e.preventDefault();
      if (to) { focusOption(p, to); }
    }
  };

  // ---- date and period: a calendar
  function dateValue(p) {
    var raw = (p.input.value || '').trim(), flex = 0;
    var fm = /~(\d+)$/.exec(raw);
    if (fm) { flex = +fm[1]; raw = raw.replace(/~\d+$/, ''); }
    if (p.kind === 'date') { return { start: dateOf(raw), end: null, flex: 0 }; }
    var parts = raw.split('..');
    return { start: dateOf(parts[0]), end: dateOf(parts[1]), flex: flex };
  }
  function flexText(n) { return n === 1 ? pkText('flexDay') : pkText('flexDays', n); }
  function daysBetween(a, b) { return Math.round((dayStart(b) - dayStart(a)) / 86400000) + 1; }
  function rangeValue(s) { return isoOf(s.start) + '..' + isoOf(s.end) + (s.flex > 0 ? '~' + s.flex : ''); }

  function calMonth(p, first, index, count) {
    var s = p.state, f = formats(), title = f.month.format(first);
    var lead = (first.getDay() - s.weekStart + 7) % 7;
    var total = new Date(first.getFullYear(), first.getMonth() + 1, 0).getDate();
    function nav(dir, name, ic) {
      // no way to a month in which every day is outside data-min or data-max
      var off = dir < 0 ? s.min && new Date(first.getFullYear(), first.getMonth(), 0) < s.min : s.max && new Date(first.getFullYear(), first.getMonth() + 1, 1) > s.max;
      return '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm" data-cal-nav="' + dir + '" aria-label="' + esc(pkText(name)) + '"' + (off ? ' disabled' : '') + '>' + icon(ic) + '</button>';
    }
    var h = '<div class="mat-cal__month"><div class="mat-cal__head">' +
      (index === 0 ? nav(-1, 'prevMonth', 'chevron-left') : '<span class="mat-cal__nav-gap"></span>') +
      '<span class="mat-cal__title" aria-live="polite">' + esc(title) + '</span>' +
      (index === count - 1 ? nav(1, 'nextMonth', 'chevron-right') : '<span class="mat-cal__nav-gap"></span>') +
      '</div><div class="mat-cal__grid" role="grid" aria-label="' + esc(title) + '"><div class="mat-cal__row" role="row">';
    for (var w = 0; w < 7; w += 1) {
      var ref = new Date(2024, 0, 1 + (s.weekStart + 6 + w) % 7);   // 2024-01-01 is a Monday
      h += '<span class="mat-cal__dow" role="columnheader" aria-label="' + esc(f.dowLong.format(ref)) + '">' + esc(f.dow.format(ref)) + '</span>';
    }
    h += '</div>';
    var cells = lead + total;
    for (var c = 0; c < Math.ceil(cells / 7) * 7; c += 1) {
      if (c % 7 === 0) { h += '<div class="mat-cal__row" role="row">'; }
      var n = c - lead + 1;
      if (n < 1 || n > total) { h += '<span class="mat-cal__blank" role="gridcell" aria-hidden="true"></span>'; }
      else {
        var d = new Date(first.getFullYear(), first.getMonth(), n);
        var off = (s.min && d < s.min) || (s.max && d > s.max);
        h += '<button type="button" class="mat-cal__day" role="gridcell" data-date="' + isoOf(d) + '" tabindex="-1" aria-selected="false" aria-label="' + esc(f.full.format(d)) + '"' + (off ? ' disabled' : '') + '>' + n + '</button>';
      }
      if (c % 7 === 6) { h += '</div>'; }
    }
    return h + '</div></div>';
  }

  function calPaint(p) {
    var s = p.state, range = p.kind === 'daterange', today = isoOf(dayStart(new Date()));
    var hi = range && s.start && !s.end && s.hover && s.hover > s.start ? s.hover : s.end;
    var startIso = s.start ? isoOf(s.start) : '', hiIso = hi ? isoOf(hi) : '', focusIso = isoOf(s.focus);
    $$('.mat-cal__day', p.body).forEach(function (b) {
      var iso = b.getAttribute('data-date');
      var isStart = iso === startIso, isEnd = range && iso === hiIso;
      var inside = range && !!s.start && !!hi && iso > startIso && iso < hiIso;
      var span = range && !!hi && hiIso > startIso;
      b.classList.toggle('is-today', iso === today);
      b.classList.toggle('is-selected', isStart || isEnd);
      b.classList.toggle('is-range-start', span && isStart);
      b.classList.toggle('is-range-end', span && isEnd);
      b.classList.toggle('is-in-range', inside);
      b.setAttribute('aria-selected', isStart || isEnd || inside ? 'true' : 'false');
      b.tabIndex = iso === focusIso ? 0 : -1;
    });
    var status = $('.mat-picker__status', p.body);
    if (status && range) {
      var f = formats().day;
      status.textContent = !s.start ? pkText('startDate') : !s.end ? pkText('endDate')
        : f.format(s.start) + ' – ' + f.format(s.end) + ' · ' + (daysBetween(s.start, s.end) === 1 ? pkText('oneDay') : pkText('daysCount', daysBetween(s.start, s.end)));
    }
    $$('[data-cal-flex]', p.body).forEach(function (b) {
      var on = +b.getAttribute('data-cal-flex') === s.flex;
      b.setAttribute('aria-checked', on ? 'true' : 'false');
      b.classList.toggle('is-active', on);
      b.tabIndex = on ? 0 : -1;
    });
  }

  function calRender(p, refocus) {
    var s = p.state, box = $('.mat-cal__months', p.body), html = '';
    var last = addMonths(s.view, s.months - 1);
    if (s.focus < s.view) { s.view = firstOf(s.focus); }
    else if (s.focus >= new Date(last.getFullYear(), last.getMonth() + 1, 1)) { s.view = firstOf(addMonths(firstOf(s.focus), -(s.months - 1))); }
    for (var i = 0; i < s.months; i += 1) { html += calMonth(p, new Date(s.view.getFullYear(), s.view.getMonth() + i, 1), i, s.months); }
    box.innerHTML = html;
    calPaint(p);
    if (refocus) { var b = $(refocus, p.body); if (b) { b.focus({ preventScroll: true }); } }
    p.relayout();
  }

  function calPick(p, d) {
    var s = p.state;
    s.focus = d;
    if (p.kind === 'date') { setPickerValue(p, isoOf(d)); closePicker(true); return; }
    if (!s.start || s.end || d < s.start) { s.start = d; s.end = null; s.hover = null; } else { s.end = d; }
    calPaint(p);
    refreshFoot(p);
    // a period without flexible dates is complete with its second day
    if (s.end && p.flexList.length === 0) { setPickerValue(p, rangeValue(s)); closePicker(true); }
  }

  var CAL_FOCUS = '.mat-cal__day[tabindex="0"]';

  function calDisplay(p, out) {
    var v = dateValue(p), f = formats().day;
    if (!p.input.value) { out.textContent = placeholderOf(p); return; }
    if (!v.start) { out.textContent = p.input.value; return; }
    var text = f.format(v.start);
    if (p.kind === 'daterange') { text += ' – ' + (v.end ? f.format(v.end) : '…') + (v.flex ? ' (' + flexText(v.flex) + ')' : ''); }
    out.textContent = text;
  }

  function calOpen(p) {
    var range = p.kind === 'daterange', v = dateValue(p);
    var min = dateOf(p.wrap.getAttribute('data-min')), max = dateOf(p.wrap.getAttribute('data-max'));
    var focus = limitDate(v.start || dayStart(new Date()), min, max);
    p.flexList = (p.wrap.getAttribute('data-flex') || '').split(',').map(function (x) { return parseInt(x, 10); }).filter(function (n) { return n > 0; });
    p.state = {
      start: v.start, end: v.end, flex: v.flex, focus: focus, hover: null, min: min, max: max, view: firstOf(focus),
      weekStart: parseInt(p.wrap.getAttribute('data-week-start') || root.getAttribute('data-week-start') || '1', 10) % 7,
      months: range && !p.full ? 2 : 1
    };
    var chips = '';
    if (range && p.flexList.length) {
      chips = '<div class="mat-picker__chips" role="radiogroup" aria-label="' + esc(pkText('flexible')) + '">' +
        [0].concat(p.flexList).map(function (n) {
          return '<button type="button" class="mat-btn mat-btn--secondary mat-btn--sm" role="radio" aria-checked="false" data-cal-flex="' + n + '">' + esc(n === 0 ? pkText('exactDates') : flexText(n)) + '</button>';
        }).join('') + '</div>';
    }
    p.body.innerHTML = '<div class="mat-cal"><div class="mat-cal__months"></div>' +
      (range ? '<p class="mat-picker__status" aria-live="polite"></p>' : '') + chips +
      '<div class="mat-cal__tools"><button type="button" class="mat-btn mat-btn--ghost mat-btn--sm" data-cal-today>' + esc(pkText('today')) + '</button></div></div>';
    setFoot(p, range && p.flexList.length ? 'apply' : null);
    calRender(p, null);
    refreshFoot(p);
    return $(CAL_FOCUS, p.body);
  }

  function calClick(p, t) {
    var s = p.state;
    var nav = t.closest('[data-cal-nav]');
    if (nav) {
      var dir = +nav.getAttribute('data-cal-nav');
      s.view = addMonths(s.view, dir);
      s.focus = limitDate(addMonths(s.focus, dir), s.min, s.max);
      calRender(p, '[data-cal-nav="' + dir + '"]');
      return;
    }
    var day = t.closest('.mat-cal__day');
    if (day && !day.disabled) { calPick(p, dateOf(day.getAttribute('data-date'))); return; }
    if (t.closest('[data-cal-today]')) {
      s.focus = limitDate(dayStart(new Date()), s.min, s.max);
      s.view = firstOf(s.focus);
      calRender(p, CAL_FOCUS);
      return;
    }
    var chip = t.closest('[data-cal-flex]');
    if (chip) { s.flex = +chip.getAttribute('data-cal-flex'); calPaint(p); }
  }

  function calKeys(p, e) {
    var s = p.state;
    if (!e.target.closest('.mat-cal__day')) { return; }
    var cur = s.focus, back = (cur.getDay() - s.weekStart + 7) % 7, to;
    if (e.key === 'ArrowLeft') { to = addDays(cur, -1); }
    else if (e.key === 'ArrowRight') { to = addDays(cur, 1); }
    else if (e.key === 'ArrowUp') { to = addDays(cur, -7); }
    else if (e.key === 'ArrowDown') { to = addDays(cur, 7); }
    else if (e.key === 'Home') { to = addDays(cur, -back); }
    else if (e.key === 'End') { to = addDays(cur, 6 - back); }
    else if (e.key === 'PageUp') { to = addMonths(cur, e.shiftKey ? -12 : -1); }
    else if (e.key === 'PageDown') { to = addMonths(cur, e.shiftKey ? 12 : 1); }
    else { return; }
    e.preventDefault();
    s.focus = limitDate(to, s.min, s.max);
    if (p.kind === 'daterange' && s.start && !s.end) { s.hover = s.focus; }
    var end = addMonths(s.view, s.months);
    if (s.focus >= s.view && s.focus < new Date(end.getFullYear(), end.getMonth(), 1)) {
      calPaint(p);
      var b = $(CAL_FOCUS, p.body);
      if (b) { b.focus({ preventScroll: true }); }
    } else { calRender(p, CAL_FOCUS); }
  }

  function calHover(p, t) {
    var s = p.state, day = t.closest && t.closest('.mat-cal__day');
    if (!day || p.kind !== 'daterange' || !s.start || s.end) { return; }
    var d = dateOf(day.getAttribute('data-date'));
    if (!sameHover(s.hover, d)) { s.hover = d; calPaint(p); }
  }
  function sameHover(a, b) { return !!a && !!b && isoOf(a) === isoOf(b); }

  KINDS.date = {
    icon: 'calendar',
    init: function () { /* the panel is built when it opens */ },
    display: calDisplay,
    open: calOpen,
    click: calClick,
    keys: calKeys,
    hover: calHover,
    ready: function () { return true; },
    value: function () { return null; }
  };
  KINDS.daterange = {
    icon: 'calendar',
    init: function () { /* the panel is built when it opens */ },
    display: calDisplay,
    open: calOpen,
    click: calClick,
    keys: calKeys,
    hover: calHover,
    ready: function (p) { return !!p.state && !!p.state.start && !!p.state.end; },
    value: function (p) { return rangeValue(p.state); }
  };

  // ---- time and time range: columns of hours and minutes, and a field to type in
  function timeNow(p) {
    var d = new Date(), step = p.state.step;
    var m = Math.round(d.getMinutes() / step) * step;
    return { h: (d.getHours() + (m >= 60 ? 1 : 0)) % 24, m: m % 60 };
  }
  function timeCurrent(p) { return p.state.parts[p.state.active]; }
  function timeSetPart(p, field, value) {
    var s = p.state, t = timeCurrent(p) || timeNow(p);
    t = { h: t.h, m: t.m };
    if (field === 'h') { t.h = value; }
    else if (field === 'hd') { t.h = value % 12 + (t.h >= 12 ? 12 : 0); }
    else if (field === 'm') { t.m = value; }
    else if (field === 'ap') { t.h = t.h % 12 + (value === 'pm' ? 12 : 0); }
    s.parts[s.active] = t;
    timePaint(p, false);
  }

  function timeColumn(name, label, items) {
    return '<div class="mat-timecol" role="listbox" aria-label="' + esc(label) + '" data-time-col="' + name + '">' +
      items.map(function (it) { return '<button type="button" class="mat-timecol__item" role="option" tabindex="-1" aria-selected="false" data-v="' + it.v + '">' + esc(it.text) + '</button>'; }).join('') + '</div>';
  }

  function timeDuration(a, b) {
    var diff = (b.h * 60 + b.m) - (a.h * 60 + a.m), next = false;
    if (diff < 0) { diff += 1440; next = true; }
    var h = Math.floor(diff / 60), m = diff % 60;
    var text = h && m ? pkText('durHM', h, m) : h ? pkText('durH', h) : pkText('durM', m);
    return next ? text + ' (' + pkText('nextDay') + ')' : text;
  }

  function timePaint(p, keepEntry, center) {
    var s = p.state, t = timeCurrent(p);
    $$('.mat-timecol', p.body).forEach(function (col) {
      var name = col.getAttribute('data-time-col'), want = null;
      if (t) {
        want = name === 'm' ? String(t.m) : name === 'ap' ? (t.h >= 12 ? 'pm' : 'am') : name === 'hd' ? String(t.h % 12 === 0 ? 12 : t.h % 12) : String(t.h);
      }
      var picked = null, first = null;
      $$('.mat-timecol__item', col).forEach(function (it) {
        if (!first) { first = it; }
        var on = it.getAttribute('data-v') === want;
        it.setAttribute('aria-selected', on ? 'true' : 'false');
        it.tabIndex = -1;
        if (on) { picked = it; }
      });
      var stop = picked || first;
      if (stop) { stop.tabIndex = 0; }
      if (picked) { if (center) { col.scrollTop = picked.offsetTop - (col.clientHeight - picked.offsetHeight) / 2; } else { keepInView(col, picked); } }
    });
    var entry = $('[data-time-entry]', p.body);
    if (entry && !keepEntry) { entry.value = t ? timeText(p, t) : ''; entry.removeAttribute('aria-invalid'); }
    if (s.range) {
      $$('[data-time-part]', p.body).forEach(function (b) {
        var part = b.getAttribute('data-time-part'), on = part === s.active, pt = s.parts[part];
        b.classList.toggle('is-active', on);
        b.setAttribute('aria-pressed', on ? 'true' : 'false');
        $('strong', b).textContent = pt ? timeText(p, pt) : '--:--';
      });
      var status = $('.mat-picker__status', p.body);
      if (status) { status.textContent = s.parts.from && s.parts.to ? timeDuration(s.parts.from, s.parts.to) : ''; }
    }
    refreshFoot(p);
  }

  function timeDisplay(p, out) {
    var parts = p.input.value.split('..'), a = timeOf(parts[0]), b = timeOf(parts[1]);
    if (!p.input.value) { out.textContent = placeholderOf(p); return; }
    if (!a) { out.textContent = p.input.value; return; }
    out.textContent = timeText(p, a) + (p.kind === 'timerange' ? ' – ' + (b ? timeText(p, b) : '…') : '');
  }

  function timeOpen(p) {
    var range = p.kind === 'timerange', parts = p.input.value.split('..');
    p.state = { range: range, active: 'from', step: Math.max(1, parseInt(p.wrap.getAttribute('data-step') || '5', 10) || 5), parts: { from: timeOf(parts[0]), to: range ? timeOf(parts[1]) : null } };
    var s = p.state, hours = [], minutes = [];
    if (p.hour12) { for (var i = 1; i <= 12; i += 1) { hours.push({ v: i, text: p2(i) }); } hours.unshift(hours.pop()); }
    else { for (var j = 0; j < 24; j += 1) { hours.push({ v: j, text: p2(j) }); } }
    for (var m = 0; m < 60; m += s.step) { minutes.push({ v: m, text: p2(m) }); }
    [s.parts.from, s.parts.to].forEach(function (t) {
      if (t && minutes.every(function (x) { return x.v !== t.m; })) { minutes.push({ v: t.m, text: p2(t.m) }); }
    });
    minutes.sort(function (a, b) { return a.v - b.v; });
    var id = nextId('mat-tm');
    p.body.innerHTML = '<div class="mat-time">' +
      (range ? '<div class="mat-btn-group" role="group" aria-label="' + esc(pkText('phTimeRange')) + '">' +
        '<button type="button" class="mat-btn mat-btn--secondary is-active" data-time-part="from" aria-pressed="true">' + esc(pkText('from')) + '&nbsp;<strong></strong></button>' +
        '<button type="button" class="mat-btn mat-btn--secondary" data-time-part="to" aria-pressed="false">' + esc(pkText('to')) + '&nbsp;<strong></strong></button></div>' : '') +
      '<input class="mat-input mat-time__entry" id="' + id + '" inputmode="numeric" autocomplete="off" data-time-entry aria-label="' + esc(pkText('typeTime')) + '" placeholder="' + (p.hour12 ? 'hh:mm am' : 'hh:mm') + '">' +
      '<div class="mat-time__cols">' + timeColumn(p.hour12 ? 'hd' : 'h', pkText('hour'), hours) + timeColumn('m', pkText('minute'), minutes) +
      (p.hour12 ? timeColumn('ap', pkText('ampm'), [{ v: 'am', text: 'AM' }, { v: 'pm', text: 'PM' }]) : '') + '</div>' +
      (range ? '<p class="mat-picker__status" aria-live="polite"></p>' : '') +
      '<div class="mat-cal__tools"><button type="button" class="mat-btn mat-btn--ghost mat-btn--sm" data-time-now>' + esc(pkText('now')) + '</button></div></div>';
    setFoot(p, 'apply');
    timePaint(p, false, false);
    // typing is quicker on a wide screen; a phone would show its keyboard, so the focus goes to the chosen hour there
    return p.full ? $('.mat-timecol__item[tabindex="0"]', p.body) : $('[data-time-entry]', p.body);
  }

  KINDS.time = KINDS.timerange = {
    icon: 'clock',
    init: function (p) {
      var attr = p.wrap.getAttribute('data-hour12');
      p.hour12 = attr ? attr === 'true' : !!new Intl.DateTimeFormat(pkLocale(), { hour: 'numeric' }).resolvedOptions().hour12;
    },
    display: timeDisplay,
    open: timeOpen,
    shown: function (p) { timePaint(p, true, true); },
    ready: function (p) { var s = p.state; return !!s && !!s.parts.from && (!s.range || !!s.parts.to); },
    value: function (p) { var s = p.state; return s.range ? timeStr(s.parts.from) + '..' + timeStr(s.parts.to) : timeStr(s.parts.from); },
    click: function (p, t) {
      var s = p.state, item = t.closest('.mat-timecol__item');
      if (item) {
        var name = item.parentNode.getAttribute('data-time-col'), v = item.getAttribute('data-v');
        timeSetPart(p, name, name === 'ap' ? v : +v);
        item.focus({ preventScroll: true });
        return;
      }
      var part = t.closest('[data-time-part]');
      if (part) { s.active = part.getAttribute('data-time-part'); timePaint(p, false); return; }
      if (t.closest('[data-time-now]')) {
        s.parts[s.active] = timeNow(p);
        timePaint(p, false);
        var picked = $('.mat-timecol__item[tabindex="0"]', p.body);
        if (picked) { picked.focus({ preventScroll: true }); }
      }
    },
    input: function (p, t) {
      if (!t.hasAttribute('data-time-entry')) { return; }
      var parsed = typedTime(t.value);
      if (t.value.trim() === '') { t.removeAttribute('aria-invalid'); return; }
      if (!parsed) { t.setAttribute('aria-invalid', 'true'); return; }
      t.removeAttribute('aria-invalid');
      p.state.parts[p.state.active] = parsed;
      timePaint(p, true);
    },
    keys: function (p, e) {
      var t = e.target;
      if (t.hasAttribute && t.hasAttribute('data-time-entry')) {
        if (e.key === 'Enter') {
          e.preventDefault();
          var parsed = typedTime(t.value);
          // text that is not a time is not taken: the field says so and the panel stays
          if (t.value.trim() !== '' && !parsed) { t.setAttribute('aria-invalid', 'true'); return; }
          if (parsed) { p.state.parts[p.state.active] = parsed; }
          timePaint(p, false);
          if (p.state.range && p.state.active === 'from' && !p.state.parts.to) { p.state.active = 'to'; timePaint(p, false); return; }
          panelAction(p, 'apply');
        }
        return;
      }
      var item = t.closest('.mat-timecol__item');
      if (!item) { return; }
      var items = $$('.mat-timecol__item', item.parentNode), i = items.indexOf(item), to = null;
      if (e.key === 'ArrowDown') { to = items[Math.min(items.length - 1, i + 1)]; }
      else if (e.key === 'ArrowUp') { to = items[Math.max(0, i - 1)]; }
      else if (e.key === 'Home') { to = items[0]; }
      else if (e.key === 'End') { to = items[items.length - 1]; }
      else if (e.key === 'PageDown') { to = items[Math.min(items.length - 1, i + 6)]; }
      else if (e.key === 'PageUp') { to = items[Math.max(0, i - 6)]; }
      else { return; }
      e.preventDefault();
      // the choice follows the focus, like in a native select
      p.spec.click(p, to);
    }
  };

  // ---- colour: swatches, and the free choice with an area, a hue and a hex value
  function swatchList(p) {
    var raw = p.wrap.getAttribute('data-swatches');
    if (raw === null) { return PK_SWATCHES.map(function (h) { return { hex: h, name: h.toUpperCase() }; }); }
    return raw.split(',').map(function (x) {
      var cut = x.indexOf(':'), hex = normHex(cut < 0 ? x : x.slice(0, cut));
      return hex ? { hex: hex, name: cut < 0 ? hex.toUpperCase() : x.slice(cut + 1).trim() } : null;
    }).filter(Boolean);
  }

  function colourFromHsv(p) {
    p.state.hex = hsvToHex(p.state.h, p.state.s, p.state.v);
    colourPaint(p, false);
  }

  function colourPaint(p, keepHex) {
    var s = p.state, hex = s.hex, on = textOn(hex), box = $('.mat-colour', p.body);
    box.style.setProperty('--mat-hue', hsvToHex(s.h, 1, 1));
    box.style.setProperty('--mat-x', (s.s * 100) + '%');
    box.style.setProperty('--mat-y', ((1 - s.v) * 100) + '%');
    box.style.setProperty('--mat-swatch', hex);
    box.style.setProperty('--mat-swatch-on', on.color);
    var area = $('.mat-colour__area', box);
    area.setAttribute('aria-valuenow', String(Math.round(s.s * 100)));
    area.setAttribute('aria-valuetext', pkText('areaValue', Math.round(s.s * 100), Math.round(s.v * 100)));
    $('.mat-colour__hue', box).value = String(Math.round(s.h));
    var field = $('.mat-colour__hex', box);
    if (!keepHex) { field.value = hex; field.removeAttribute('aria-invalid'); }
    $('.mat-colour__contrast span:last-child', box).textContent = pkText(on.name) + ': ' + on.ratio.toFixed(1) + ' : 1 · ' + pkText(levelOf(on.ratio));
    $$('.mat-colour__swatch', box).forEach(function (b) { b.setAttribute('aria-pressed', b.getAttribute('data-hex') === hex ? 'true' : 'false'); });
  }

  function colourSet(p, hex, keepHex) {
    var h = hexToHsv(hex);
    if (h.s !== 0 && h.v !== 0) { p.state.h = h.h; }
    p.state.s = h.s;
    p.state.v = h.v;
    p.state.hex = hex;
    colourPaint(p, keepHex);
  }

  KINDS.colour = {
    icon: 'palette',
    init: function () { /* the panel is built when it opens */ },
    display: function (p, out) {
      var hex = normHex(p.input.value);
      if (!p.input.value) { out.textContent = placeholderOf(p); return; }
      if (!hex) { out.textContent = p.input.value; return; }
      var chip = doc.createElement('span');
      chip.className = 'mat-picker__swatch';
      chip.style.setProperty('--mat-swatch', hex);
      var text = doc.createElement('span');
      text.className = 'mat-mono';
      text.textContent = hex.toUpperCase();
      out.appendChild(chip);
      out.appendChild(text);
    },
    open: function (p) {
      var hex = normHex(p.input.value) || '#2563eb', start = hexToHsv(hex);
      p.state = { h: start.h, s: start.s, v: start.v, hex: hex };
      var swatches = swatchList(p);
      p.body.innerHTML = '<div class="mat-colour">' +
        (swatches.length ? '<div class="mat-colour__swatches" role="group" aria-label="' + esc(pkText('colours')) + '">' + swatches.map(function (sw) {
          return '<button type="button" class="mat-colour__swatch" tabindex="-1" aria-pressed="false" aria-label="' + esc(sw.name) + '" data-hex="' + sw.hex + '">' + icon('check') + '</button>';
        }).join('') + '</div>' : '') +
        '<div class="mat-colour__area" tabindex="0" role="slider" aria-label="' + esc(pkText('area')) + '" aria-valuemin="0" aria-valuemax="100"><span class="mat-colour__thumb"></span></div>' +
        '<input type="range" class="mat-colour__hue" min="0" max="360" step="1" aria-label="' + esc(pkText('hue')) + '">' +
        '<div class="mat-colour__row"><span class="mat-colour__preview"></span><input class="mat-input mat-colour__hex" maxlength="7" autocomplete="off" spellcheck="false" aria-label="' + esc(pkText('hex')) + '"></div>' +
        '<p class="mat-colour__contrast"><span class="mat-colour__sample" aria-hidden="true">Aa</span><span></span></p></div>';
      $$('.mat-colour__swatch', p.body).forEach(function (b) {
        var hx = b.getAttribute('data-hex');
        b.style.setProperty('--mat-swatch', hx);
        b.style.setProperty('--mat-swatch-on', textOn(hx).color);
      });
      setFoot(p, 'apply');
      colourPaint(p, false);
      var first = $('.mat-colour__swatch[aria-pressed="true"]', p.body) || $('.mat-colour__swatch', p.body);
      if (first) { first.tabIndex = 0; }
      return first || $('.mat-colour__area', p.body);
    },
    ready: function (p) { return !!p.state; },
    value: function (p) { return p.state.hex; },
    click: function (p, t) {
      var sw = t.closest('.mat-colour__swatch');
      if (sw) { setPickerValue(p, sw.getAttribute('data-hex')); closePicker(true); }
    },
    input: function (p, t) {
      if (t.classList.contains('mat-colour__hue')) { p.state.h = +t.value; colourFromHsv(p); }
      else if (t.classList.contains('mat-colour__hex')) {
        var hex = normHex(t.value);
        if (hex && (t.value.replace('#', '').length === 6)) { t.removeAttribute('aria-invalid'); colourSet(p, hex, true); }
        else { t.setAttribute('aria-invalid', t.value === '' ? 'false' : 'true'); }
      }
    },
    keys: function (p, e) {
      var t = e.target, s = p.state;
      if (t.classList.contains('mat-colour__hex')) {
        if (e.key === 'Enter') { e.preventDefault(); var hex = normHex(t.value); if (hex) { colourSet(p, hex, false); panelAction(p, 'apply'); } }
        return;
      }
      if (t.classList.contains('mat-colour__area')) {
        var step = e.shiftKey ? 0.1 : 0.01;
        if (e.key === 'ArrowLeft') { s.s = clamp(s.s - step, 0, 1); }
        else if (e.key === 'ArrowRight') { s.s = clamp(s.s + step, 0, 1); }
        else if (e.key === 'ArrowUp') { s.v = clamp(s.v + step, 0, 1); }
        else if (e.key === 'ArrowDown') { s.v = clamp(s.v - step, 0, 1); }
        else { return; }
        e.preventDefault();
        colourFromHsv(p);
        return;
      }
      var sw = t.closest('.mat-colour__swatch');
      if (!sw) { return; }
      var all = $$('.mat-colour__swatch', p.body), i = all.indexOf(sw), per = all.filter(function (b) { return b.offsetTop === all[0].offsetTop; }).length || 1, to = null;
      if (e.key === 'ArrowRight') { to = all[Math.min(all.length - 1, i + 1)]; }
      else if (e.key === 'ArrowLeft') { to = all[Math.max(0, i - 1)]; }
      else if (e.key === 'ArrowDown') { to = all[Math.min(all.length - 1, i + per)]; }
      else if (e.key === 'ArrowUp') { to = all[Math.max(0, i - per)]; }
      else if (e.key === 'Home') { to = all[0]; }
      else if (e.key === 'End') { to = all[all.length - 1]; }
      else { return; }
      e.preventDefault();
      all.forEach(function (b) { b.tabIndex = -1; });
      to.tabIndex = 0;
      to.focus({ preventScroll: true });
    }
  };

  // ---- tags: a field of chips that the user types into
  // The real input holds the tags joined by data-sep (default ", "). The script draws a field (.mat-tagfield) with a chip per tag and a text field to type in.
  // Enter or a comma adds what is typed, Backspace in the empty field removes the last chip, text that is pasted is split at commas and line breaks, and what is
  // typed when the field is left becomes a tag, so that it is not lost when the form is sent. A tag that is there is not added again (data-duplicates allows it),
  // data-max limits the number of tags and data-max-length the length of one. The options of the wrapper (.mat-picker__options, as in a select) are suggestions
  // for the text field; with data-strict only they can be added. There is no panel to open: the suggestions are a list under the field.
  var tagsOpen = null;

  function tagSep(p) { return p.sep.trim() || p.sep; }
  function tagList(p) { return p.input.value.split(tagSep(p)).map(function (s) { return s.trim(); }).filter(Boolean); }
  // a comma or a line break always starts the next tag; the separator of the value can not be in a tag
  function tagSplit(p, text) {
    return text.split(/[\r\n\t,]+/).reduce(function (all, part) { return all.concat(part.split(tagSep(p))); }, []);
  }
  function tagClean(p, text) { return text.replace(/\s+/g, ' ').trim().slice(0, p.maxLength).trim(); }
  function tagOptions(p) { return p.list ? $$('.mat-picker__option', p.list) : []; }
  function tagOptionValue(o) { return o.getAttribute('data-value') || o.textContent.trim(); }
  function tagSuggestion(p, text) {
    var t = text.toLowerCase();
    return tagOptions(p).filter(function (o) { return tagOptionValue(o).toLowerCase() === t; })[0] || null;
  }

  // what is said under the field, and to a screen reader
  function tagsStatus(p, text) {
    clearTimeout(p.statusTimer);
    p.status.textContent = text || '';
    p.status.hidden = !text;
    p.live.textContent = text || '';
    if (text) { p.statusTimer = setTimeout(function () { tagsStatus(p, ''); }, 4000); }
  }

  // The chips and the state of the field, drawn from the value of the real input (also after a reset, a script or a form that fills it).
  function tagsSync(p) {
    var vals = tagList(p), off = p.input.disabled, ro = p.input.readOnly, locked = off || ro;
    $$('.mat-chip', p.field).forEach(function (c) { c.remove(); });
    vals.forEach(function (v) {
      var chip = doc.createElement('span');
      chip.className = 'mat-chip';
      chip.setAttribute('data-tag', v);
      var text = doc.createElement('span');
      text.className = 'mat-chip__text';
      text.textContent = v;
      chip.appendChild(text);
      if (!locked) {
        var x = doc.createElement('button');
        x.type = 'button';
        x.className = 'mat-chip__x';
        x.setAttribute('aria-label', pkText('removeTag', v));
        x.innerHTML = icon('x');
        chip.appendChild(x);
      }
      p.field.insertBefore(chip, p.typing);
    });
    p.field.classList.toggle('is-disabled', off);
    p.field.classList.toggle('is-readonly', ro);
    p.field.classList.toggle('is-full', p.max > 0 && vals.length >= p.max);
    p.typing.disabled = off;
    p.typing.hidden = ro;
    p.typing.placeholder = vals.length === 0 && !locked ? placeholderOf(p) : '';
    if (locked) { tagsClose(p); }
  }

  function tagsSet(p, list) {
    var v = list.join(p.sep);
    if (p.input.value === v) { tagsSync(p); return; }
    p.input.value = v;
    tagsSync(p);
    p.input.dispatchEvent(new Event('input', { bubbles: true }));
    p.input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  // Adds what the text holds and returns the parts that were refused, so that the caller leaves them in the text field for the user to correct;
  // what was refused is said under the field.
  function tagsAdd(p, text) {
    var vals = tagList(p), refused = [], message = '', added = 0;
    tagSplit(p, text).forEach(function (raw) {
      var t = tagClean(p, raw);
      if (!t) { return; }
      var known = tagSuggestion(p, t);
      if (p.strict && !known) { message = pkText('tagStrict'); refused.push(t); return; }
      if (known) { t = tagOptionValue(known); }
      if (p.unique && vals.some(function (x) { return x.toLowerCase() === t.toLowerCase(); })) { message = pkText('tagDuplicate', t); refused.push(t); return; }
      if (p.max > 0 && vals.length >= p.max) { message = pkText('tagMax', p.max); refused.push(t); return; }
      vals.push(t);
      added += 1;
    });
    if (added) { tagsSet(p, vals); }
    tagsStatus(p, message);
    return refused;
  }

  // "typing": the focus goes to the text field, "x": to the next remove button (the keyboard stays where it was)
  function tagsRemove(p, tag, focus) {
    var vals = tagList(p), i = vals.indexOf(tag);
    if (i < 0) { return; }
    vals.splice(i, 1);
    tagsSet(p, vals);
    tagsStatus(p, pkText('tagRemoved', tag));
    var xs = $$('.mat-chip__x', p.field);
    (focus === 'x' && xs.length ? xs[Math.min(i, xs.length - 1)] : p.typing).focus({ preventScroll: true });
    if (p.suggest && !p.suggest.hidden) { tagsSuggest(p); }
  }

  // ---- the suggestions
  function tagsFilter(p) {
    var q = p.typing.value.trim().toLowerCase();
    var taken = tagList(p).map(function (s) { return s.toLowerCase(); }), shown = 0;
    tagOptions(p).forEach(function (o) {
      var v = tagOptionValue(o).toLowerCase();
      var hit = taken.indexOf(v) < 0 && (!q || (o.textContent + ' ' + v + ' ' + (o.getAttribute('data-keywords') || '')).toLowerCase().indexOf(q) >= 0);
      o.hidden = !hit;
      if (hit) { shown += 1; }
    });
    p.empty.hidden = !(p.strict && q && shown === 0);
    return shown;
  }
  function tagsVisible(p) { return $$('.mat-picker__option:not([hidden])', p.list); }
  function tagsActive(p, o) {
    $$('.mat-picker__option.is-active', p.list).forEach(function (x) { x.classList.remove('is-active'); x.removeAttribute('aria-selected'); });
    if (!o) { p.typing.removeAttribute('aria-activedescendant'); return; }
    o.classList.add('is-active');
    o.setAttribute('aria-selected', 'true');
    p.typing.setAttribute('aria-activedescendant', o.id);
    keepInView(p.list, o);
  }
  function tagsPlace(p) {
    var r = p.field.getBoundingClientRect(), panel = p.suggest, vw = window.innerWidth, vh = window.innerHeight;
    panel.style.left = '0px'; panel.style.top = '0px'; panel.style.maxHeight = '';
    panel.style.width = Math.min(r.width, vw - 16) + 'px';
    var h = panel.offsetHeight, below = vh - r.bottom - 10, above = r.top - 10, top;
    if (h <= below) { top = r.bottom + 4; }
    else if (h <= above) { top = r.top - h - 4; }
    else if (below >= above) { top = r.bottom + 4; panel.style.maxHeight = Math.max(120, below) + 'px'; }
    else { var room = Math.max(120, above); panel.style.maxHeight = room + 'px'; top = r.top - Math.min(h, room) - 4; }
    panel.style.left = Math.max(8, Math.min(r.left, vw - panel.offsetWidth - 8)) + 'px';
    panel.style.top = Math.max(8, top) + 'px';
  }
  function tagsClose(p) {
    if (!p.suggest) { return; }
    p.suggest.hidden = true;
    p.typing.setAttribute('aria-expanded', 'false');
    p.typing.removeAttribute('aria-activedescendant');
    if (tagsOpen === p) { tagsOpen = null; }
  }
  // (Re)draws the list for what is typed and opens it when there is something to show. In a strict field the first match is the one Enter takes.
  function tagsSuggest(p) {
    if (!p.suggest) { return; }
    if (p.typing.disabled || p.input.readOnly) { tagsClose(p); return; }
    var shown = tagsFilter(p);
    if (shown === 0 && p.empty.hidden) { tagsClose(p); return; }
    tagsActive(p, p.strict && p.typing.value.trim() && shown ? tagsVisible(p)[0] : null);
    p.suggest.hidden = false;
    p.typing.setAttribute('aria-expanded', 'true');
    tagsOpen = p;
    tagsPlace(p);
  }

  function tagsKeys(p, e) {
    if (e.ctrlKey || e.metaKey || e.altKey || e.isComposing) { return; }
    var t = p.typing, sep = tagSep(p), isOpen = !!p.suggest && !p.suggest.hidden;
    if (e.key === 'Enter') {
      var act = isOpen ? $('.mat-picker__option.is-active', p.list) : null;
      if (act) { e.preventDefault(); tagsAdd(p, tagOptionValue(act)); t.value = ''; tagsSuggest(p); }
      else if (t.value.trim()) { e.preventDefault(); t.value = tagsAdd(p, t.value).join(', '); tagsSuggest(p); }
      return;
    }
    if (e.key === ',' || (sep.length === 1 && e.key === sep)) {
      e.preventDefault();
      if (t.value.trim()) { t.value = tagsAdd(p, t.value).join(', '); }
      tagsSuggest(p);
      return;
    }
    if (e.key === 'Backspace' && t.value === '') {
      var vals = tagList(p);
      if (vals.length) { e.preventDefault(); tagsRemove(p, vals[vals.length - 1], 'typing'); }
      return;
    }
    if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && p.suggest) {
      e.preventDefault();
      if (!isOpen) { tagsSuggest(p); }
      var vis = tagsVisible(p), n = vis.length, cur = vis.indexOf($('.mat-picker__option.is-active', p.list));
      if (p.suggest.hidden || !n) { return; }
      tagsActive(p, vis[e.key === 'ArrowDown' ? (cur < 0 ? 0 : (cur + 1) % n) : (cur < 0 ? n - 1 : (cur - 1 + n) % n)]);
      return;
    }
    if (e.key === 'Escape' && isOpen) { e.preventDefault(); e.stopPropagation(); tagsClose(p); return; }
    if (e.key === 'ArrowLeft' && t.selectionStart === 0 && t.selectionEnd === 0) {
      var xs = $$('.mat-chip__x', p.field);
      if (xs.length) { e.preventDefault(); xs[xs.length - 1].focus(); }
    }
  }

  KINDS.tags = {
    inline: true,
    init: function (p) {
      var wrap = p.wrap, input = p.input, id = input.id;
      p.sep = wrap.hasAttribute('data-sep') ? wrap.getAttribute('data-sep') : ', ';
      p.max = parseInt(wrap.getAttribute('data-max'), 10) || 0;
      p.maxLength = parseInt(wrap.getAttribute('data-max-length'), 10) || 48;
      p.unique = !wrap.hasAttribute('data-duplicates');
      p.strict = wrap.hasAttribute('data-strict');

      p.field = doc.createElement('div');
      p.field.className = 'mat-input mat-tagfield';
      p.typing = doc.createElement('input');
      p.typing.type = 'text';
      p.typing.className = 'mat-tagfield__input';
      p.typing.id = id + '-entry';
      p.typing.setAttribute('autocomplete', 'off');
      p.typing.setAttribute('autocapitalize', 'off');
      p.typing.setAttribute('spellcheck', 'false');
      p.typing.setAttribute('enterkeyhint', 'done');
      var lab = $('label[for="' + id + '"]');
      if (lab) { if (!lab.id) { lab.id = id + '-label'; } p.typing.setAttribute('aria-labelledby', lab.id); }
      else { p.typing.setAttribute('aria-label', placeholderOf(p)); }
      if (input.getAttribute('aria-describedby')) { p.typing.setAttribute('aria-describedby', input.getAttribute('aria-describedby')); }
      p.field.appendChild(p.typing);
      input.parentNode.insertBefore(p.field, input.nextSibling);

      p.status = doc.createElement('p');
      p.status.className = 'mat-picker__status';
      p.status.setAttribute('aria-hidden', 'true');
      p.status.hidden = true;
      p.live = doc.createElement('span');
      p.live.className = 'mat-sr-only';
      p.live.setAttribute('role', 'status');
      p.live.setAttribute('aria-live', 'polite');
      p.field.parentNode.insertBefore(p.status, p.field.nextSibling);
      p.field.parentNode.insertBefore(p.live, p.status);

      p.list = $('.mat-picker__options', wrap);
      if (p.list) {
        p.list.id = p.list.id || id + '-list';
        p.list.setAttribute('role', 'listbox');
        p.list.setAttribute('aria-label', pkText('suggestions'));
        p.list.hidden = false;
        $$('.mat-picker__option', p.list).forEach(function (o, i) {
          o.type = 'button';
          o.setAttribute('role', 'option');
          o.tabIndex = -1;
          o.id = id + '-option-' + i;
        });
        p.empty = doc.createElement('p');
        p.empty.className = 'mat-picker__empty';
        p.empty.setAttribute('role', 'status');
        p.empty.hidden = true;
        p.empty.textContent = pkText('noResults');
        p.suggest = doc.createElement('div');
        p.suggest.className = 'mat-picker__panel mat-picker__panel--list mat-picker__panel--suggest';
        p.suggest.hidden = true;
        var body = doc.createElement('div');
        body.className = 'mat-picker__body';
        body.appendChild(p.list);
        body.appendChild(p.empty);
        p.suggest.appendChild(body);
        wrap.appendChild(p.suggest);
        p.typing.setAttribute('role', 'combobox');
        p.typing.setAttribute('aria-autocomplete', 'list');
        p.typing.setAttribute('aria-haspopup', 'listbox');
        p.typing.setAttribute('aria-expanded', 'false');
        p.typing.setAttribute('aria-controls', p.list.id);
        // a click on a suggestion must not take the focus from the text field
        p.suggest.addEventListener('mousedown', function (e) { e.preventDefault(); });
        p.suggest.addEventListener('click', function (e) {
          var o = e.target.closest('.mat-picker__option');
          if (!o) { return; }
          tagsAdd(p, tagOptionValue(o));
          p.typing.value = '';
          p.typing.focus({ preventScroll: true });
          tagsSuggest(p);
        });
      }

      p.field.addEventListener('click', function (e) {
        var x = e.target.closest('.mat-chip__x');
        if (x) { tagsRemove(p, x.closest('.mat-chip').getAttribute('data-tag'), e.detail === 0 ? 'x' : 'typing'); return; }
        if (p.typing.disabled || p.input.readOnly) { return; }
        p.typing.focus();
        tagsSuggest(p);
      });
      p.field.addEventListener('keydown', function (e) {
        var x = e.target.closest && e.target.closest('.mat-chip__x');
        if (!x) { return; }
        var xs = $$('.mat-chip__x', p.field), i = xs.indexOf(x);
        if (e.key === 'ArrowLeft') { e.preventDefault(); if (i > 0) { xs[i - 1].focus(); } }
        else if (e.key === 'ArrowRight') { e.preventDefault(); if (i < xs.length - 1) { xs[i + 1].focus(); } else { p.typing.focus(); } }
        else if (e.key === 'Backspace' || e.key === 'Delete') { e.preventDefault(); tagsRemove(p, x.closest('.mat-chip').getAttribute('data-tag'), 'x'); }
      });
      p.typing.addEventListener('keydown', function (e) { tagsKeys(p, e); });
      p.typing.addEventListener('input', function () { tagsStatus(p, ''); tagsSuggest(p); });
      p.typing.addEventListener('paste', function (e) {
        var text = (e.clipboardData || window.clipboardData).getData('text');
        if (!/[\r\n\t,]/.test(text) && text.indexOf(tagSep(p)) < 0) { return; }   // plain text: the field takes it as it is
        e.preventDefault();
        var all = p.typing.value.slice(0, p.typing.selectionStart) + text + p.typing.value.slice(p.typing.selectionEnd);
        p.typing.value = tagsAdd(p, all).join(', ');
        tagsSuggest(p);
      });
      wrap.addEventListener('focusout', function (e) {
        if (e.relatedTarget && wrap.contains(e.relatedTarget)) { return; }
        if (!e.relatedTarget && !doc.hasFocus()) { return; }   // the window lost the focus, not the field
        if (p.typing.value.trim()) { tagsAdd(p, p.typing.value); }
        p.typing.value = '';
        tagsClose(p);
      });
    },
    sync: tagsSync
  };

  // ---- the events of the pickers
  doc.addEventListener('click', function (e) {
    var t = e.target;
    if (!t.closest) { return; }
    var inside = t.closest('.mat-picker__panel');
    var trigger = t.closest('.mat-picker__trigger');
    if (trigger) { openPicker(trigger.closest('.mat-picker')); return; }
    var clear = t.closest('.mat-picker__clear');
    if (clear) {
      var cp = clear.closest('.mat-picker')._matPicker;
      closePicker(false);
      setPickerValue(cp, '');
      cp.trigger.focus({ preventScroll: true });
      return;
    }
    if (!inside) { closePicker(false); return; }
    var p = pickerOpen;
    if (!p || !p.panel.contains(inside)) { return; }
    var action = t.closest('[data-picker-action]');
    if (action) { panelAction(p, action.getAttribute('data-picker-action')); return; }
    if (t.closest('[data-picker-close]')) { closePicker(true); return; }
    if (p.spec.click) { p.spec.click(p, t, e); }
  });

  doc.addEventListener('input', function (e) {
    var p = pickerOpen;
    if (p && p.spec.input && e.target.closest && p.panel.contains(e.target)) { p.spec.input(p, e.target, e); }
  });

  doc.addEventListener('mouseover', function (e) {
    var p = pickerOpen;
    if (p && p.spec.hover && p.panel.contains(e.target)) { p.spec.hover(p, e.target); }
  });

  doc.addEventListener('keydown', function (e) {
    var p = pickerOpen;
    if (p && e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closePicker(true); return; }
    var t = e.target;
    if (!t.closest) { return; }
    if (t.classList.contains('mat-picker__trigger') && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) { e.preventDefault(); openPicker(t.closest('.mat-picker')); return; }
    if (!p || !p.panel.contains(t)) { return; }
    // on a phone the panel covers the page, so the Tab key stays inside it
    if (e.key === 'Tab' && p.full) {
      var tabbable = $$('button, input, [tabindex="0"]', p.panel).filter(function (el) { return !el.disabled && el.tabIndex >= 0 && el.offsetParent !== null; });
      if (tabbable.length) {
        var first = tabbable[0], last = tabbable[tabbable.length - 1];
        if (e.shiftKey && t === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && t === last) { e.preventDefault(); first.focus(); }
      }
      return;
    }
    if (p.spec.keys && !e.ctrlKey && !e.metaKey && !e.altKey) { p.spec.keys(p, e); }
  }, true);

  // a dialog around the picker is closed by Escape only after the picker
  doc.addEventListener('cancel', function (e) {
    if (pickerOpen && e.target.classList && e.target.classList.contains('mat-dialog')) { e.preventDefault(); closePicker(true); }
    else if (tagsOpen && e.target.classList && e.target.classList.contains('mat-dialog')) { e.preventDefault(); tagsClose(tagsOpen); }
  }, true);

  // the real input is not drawn; whoever focuses it (a label, a validation message) lands on the button
  doc.addEventListener('focusin', function (e) {
    var t = e.target;
    if (t.classList && t.classList.contains('mat-picker__input')) {
      var p = t.closest('.mat-picker') && t.closest('.mat-picker')._matPicker;
      if (p) { (p.spec.inline ? p.typing : p.trigger).focus({ preventScroll: false }); }
    }
  });

  doc.addEventListener('reset', function (e) {
    setTimeout(function () { $$('.mat-picker', e.target).forEach(syncPicker); }, 0);
  }, true);

  // pointer: the area of the colour picker
  doc.addEventListener('pointerdown', function (e) {
    var area = e.target.closest && e.target.closest('.mat-colour__area');
    var p = pickerOpen;
    if (!area || !p || p.kind !== 'colour') { return; }
    e.preventDefault();
    area.focus({ preventScroll: true });
    function move(ev) {
      var r = area.getBoundingClientRect();
      p.state.s = clamp((ev.clientX - r.left) / r.width, 0, 1);
      p.state.v = 1 - clamp((ev.clientY - r.top) / r.height, 0, 1);
      colourFromHsv(p);
    }
    function up() {
      area.removeEventListener('pointermove', move);
      area.removeEventListener('pointerup', up);
      area.removeEventListener('pointercancel', up);
    }
    move(e);
    if (area.setPointerCapture) { area.setPointerCapture(e.pointerId); }
    area.addEventListener('pointermove', move);
    area.addEventListener('pointerup', up);
    area.addEventListener('pointercancel', up);
  });

  window.addEventListener('resize', function () { if (pickerOpen) { pickerOpen.relayout(); syncLock(); } if (tagsOpen) { tagsPlace(tagsOpen); } });
  doc.addEventListener('scroll', function (e) {
    if (pickerOpen && !pickerOpen.full && !pickerOpen.panel.contains(e.target)) { pickerOpen.relayout(); }
    if (tagsOpen && !tagsOpen.suggest.contains(e.target)) { tagsPlace(tagsOpen); }
  }, true);

  // ------------------------------------------------------------------------------------------------ carousel (LIST-13, matui.lists.css)
  // A carousel is a strip of slides that scrolls and snaps in CSS, so it is a scrollable strip without the script:
  //   <section class="mat-carousel" data-carousel aria-label="..."><div class="mat-carousel__track"><div class="mat-carousel__slide">...</div></div></section>
  // The script names the slides ("2 of 5"), adds a bar below the strip (Previous, a dot per page, Next, and Pause when data-autoplay="ms" is set), keeps the
  // slides that are out of view inert (the Tab key never enters them) and says which page is shown. data-loop: Next on the last page goes to the first.
  // Autoplay stops while the pointer or the focus is inside, after any use of a control, and never starts for a user who asked for less motion.
  var CAR_TEXT = {
    en: { carousel: 'carousel', slide: 'slide', slides: 'Slides', prev: 'Previous', next: 'Next', goTo: 'Show {0}', slideOf: '{0} of {1}', showing: 'Showing {0} of {1}', pages: 'Choose what to show', pause: 'Pause the slide show', play: 'Start the slide show' },
    de: { carousel: 'Karussell', slide: 'Element', slides: 'Elemente', prev: 'Zurück', next: 'Weiter', goTo: '{0} zeigen', slideOf: '{0} von {1}', showing: '{0} von {1} wird gezeigt', pages: 'Anzeige wählen', pause: 'Diashow anhalten', play: 'Diashow starten' }
  };

  function carText(key, a, b) {
    var table = CAR_TEXT[(root.lang || 'en').slice(0, 2).toLowerCase()] || CAR_TEXT.en;
    var s = root.getAttribute('data-car-' + key.toLowerCase()) || table[key] || CAR_TEXT.en[key] || key;
    return s.replace('{0}', function () { return a === undefined ? '' : a; }).replace('{1}', function () { return b === undefined ? '' : b; });
  }
  function reducedMotion() { return window.matchMedia('(prefers-reduced-motion: reduce)').matches; }

  // the distance from one slide to the next, and how many fit in the strip
  function carStep(c) {
    var first = c.slides[0];
    return first ? first.getBoundingClientRect().width + c.gap : 0;
  }
  function carLeft(c, index) { return c.slides[index].offsetLeft - c.slides[0].offsetLeft; }

  function carDots(c) {
    c.dots.textContent = '';
    for (var i = 0; i < c.pages; i += 1) {
      var d = doc.createElement('button');
      d.type = 'button';
      d.className = 'mat-carousel__dot';
      d.setAttribute('data-page', String(i));
      d.setAttribute('aria-label', carText('goTo', i + 1));
      c.dots.appendChild(d);
    }
  }

  // what is shown now: the page, the slides in view (the others are inert), the state of the buttons
  function carUpdate(c) {
    c.frame = 0;
    var t = c.track, step = carStep(c), max = t.scrollWidth - t.clientWidth;
    var atStart = t.scrollLeft <= 2, atEnd = t.scrollLeft >= max - 2;
    var index = step > 0 ? Math.round(t.scrollLeft / step) : 0;
    var page = atEnd ? c.pages - 1 : atStart ? 0 : Math.min(c.pages - 1, Math.floor(index / c.per));
    var box = t.getBoundingClientRect();
    c.slides.forEach(function (s) {
      var r = s.getBoundingClientRect();
      var inView = r.width > 0 && r.right > box.left + r.width / 2 && r.left < box.right - r.width / 2;
      if (inView) { s.removeAttribute('inert'); } else { s.setAttribute('inert', ''); }
      s.classList.toggle('is-active', inView);
    });
    $$('.mat-carousel__dot', c.dots).forEach(function (d, i) {
      if (i === page) { d.setAttribute('aria-current', 'true'); } else { d.removeAttribute('aria-current'); }
    });
    c.prev.setAttribute('aria-disabled', !c.loop && atStart ? 'true' : 'false');
    c.next.setAttribute('aria-disabled', !c.loop && atEnd ? 'true' : 'false');
    if (page !== c.page) {
      c.page = page;
      // a screen reader is told what is shown, unless the show moves by itself
      if (c.ready && !c.playing) { c.live.textContent = carText('showing', page + 1, c.pages); }
    }
  }
  function carSchedule(c) { if (!c.frame) { c.frame = window.requestAnimationFrame(function () { carUpdate(c); }); } }

  function carLayout(c) {
    var t = c.track, step;
    c.gap = parseFloat(getComputedStyle(t).columnGap) || 0;
    step = carStep(c);
    c.per = step > 0 ? Math.max(1, Math.round((t.clientWidth + c.gap) / step)) : 1;
    var pages = Math.max(1, Math.ceil(c.slides.length / c.per));
    if (pages !== c.pages) { c.pages = pages; carDots(c); c.page = -1; }
    c.bar.hidden = pages <= 1 && !c.toggle;
    carUpdate(c);
  }

  function carGo(c, page, auto) {
    var index = Math.min(page * c.per, Math.max(0, c.slides.length - c.per));
    c.track.scrollTo({ left: carLeft(c, index), behavior: reducedMotion() || !c.ready ? 'auto' : 'smooth' });
    if (!auto) { carStop(c); }
  }
  function carMove(c, dir, auto) {
    var page = c.page + dir;
    if (page >= c.pages) { if (!c.loop && !auto) { return; } page = 0; }
    else if (page < 0) { if (!c.loop) { return; } page = c.pages - 1; }
    carGo(c, page, auto);
  }

  // ---- autoplay: the user stops it for good with any control, and it waits while the pointer or the focus is inside
  function carToggleButton(c) {
    if (!c.toggle) { return; }
    c.toggle.setAttribute('aria-label', carText(c.playing ? 'pause' : 'play'));
    c.toggle.innerHTML = icon(c.playing ? 'pause' : 'play');
  }
  function carStart(c) {
    if (c.playing || !c.ms) { return; }
    c.playing = true;
    c.timer = setInterval(function () {
      if (c.hover || c.focus || doc.hidden || c.track.clientWidth === 0) { return; }
      carMove(c, 1, true);
    }, c.ms);
    carToggleButton(c);
  }
  function carStop(c) {
    clearInterval(c.timer);
    c.timer = 0;
    if (c.playing) { c.playing = false; carToggleButton(c); }
  }

  function carInit(root1) {
    if (root1._matCarousel) { return; }
    var track = $('.mat-carousel__track', root1);
    var slides = $$('.mat-carousel__slide', root1);
    if (!track || slides.length === 0) { return; }
    var c = { root: root1, track: track, slides: slides, pages: 0, page: -1, per: 1, gap: 0, playing: false, hover: false, focus: false, ready: false, frame: 0 };
    root1._matCarousel = c;
    c.loop = root1.hasAttribute('data-loop');
    c.ms = parseInt(root1.getAttribute('data-autoplay'), 10) || 0;

    if (!root1.getAttribute('aria-roledescription')) { root1.setAttribute('aria-roledescription', carText('carousel')); }
    if (!root1.getAttribute('role') && root1.tagName !== 'SECTION') { root1.setAttribute('role', 'region'); }
    track.setAttribute('tabindex', '0');
    track.setAttribute('aria-label', carText('slides'));
    slides.forEach(function (s, i) {
      s.setAttribute('role', 'group');
      s.setAttribute('aria-roledescription', carText('slide'));
      if (!s.getAttribute('aria-label') && !s.getAttribute('aria-labelledby')) { s.setAttribute('aria-label', carText('slideOf', i + 1, slides.length)); }
    });

    c.bar = doc.createElement('div');
    c.bar.className = 'mat-carousel__bar';
    function button(cls, label, ic) {
      var b = doc.createElement('button');
      b.type = 'button';
      b.className = 'mat-btn mat-btn--ghost mat-btn--icon ' + cls;
      b.setAttribute('aria-label', label);
      b.innerHTML = icon(ic);
      return b;
    }
    if (c.ms) { c.toggle = button('mat-carousel__toggle', carText('pause'), 'pause'); c.bar.appendChild(c.toggle); }
    c.prev = button('mat-carousel__prev', carText('prev'), 'chevron-left');
    c.dots = doc.createElement('div');
    c.dots.className = 'mat-carousel__dots';
    c.dots.setAttribute('role', 'group');
    c.dots.setAttribute('aria-label', carText('pages'));
    c.next = button('mat-carousel__next', carText('next'), 'chevron-right');
    c.bar.appendChild(c.prev);
    c.bar.appendChild(c.dots);
    c.bar.appendChild(c.next);
    c.live = doc.createElement('span');
    c.live.className = 'mat-sr-only';
    c.live.setAttribute('role', 'status');
    c.live.setAttribute('aria-live', 'polite');
    root1.appendChild(c.bar);
    root1.appendChild(c.live);
    root1.classList.add('is-ready');

    c.bar.addEventListener('click', function (e) {
      var b = e.target.closest('button');
      if (!b) { return; }
      if (b === c.toggle) { if (c.playing) { carStop(c); } else { carStart(c); } return; }
      if (b === c.prev || b === c.next) { if (b.getAttribute('aria-disabled') !== 'true') { carStop(c); carMove(c, b === c.next ? 1 : -1, false); } return; }
      if (b.hasAttribute('data-page')) { carGo(c, parseInt(b.getAttribute('data-page'), 10), false); }
    });
    root1.addEventListener('keydown', function (e) {
      if (e.ctrlKey || e.metaKey || e.altKey || e.shiftKey) { return; }
      var t = e.target;
      // the keys belong to the strip and to the buttons of the bar, not to a field inside a slide
      if (t !== track && !c.bar.contains(t)) { return; }
      var to = e.key === 'ArrowRight' ? 'next' : e.key === 'ArrowLeft' ? 'prev' : e.key === 'Home' ? 'first' : e.key === 'End' ? 'last' : '';
      if (!to) { return; }
      e.preventDefault();
      carStop(c);
      if (to === 'next') { carMove(c, 1, false); } else if (to === 'prev') { carMove(c, -1, false); }
      else { carGo(c, to === 'first' ? 0 : c.pages - 1, false); }
    });
    root1.addEventListener('mouseenter', function () { c.hover = true; });
    root1.addEventListener('mouseleave', function () { c.hover = false; });
    root1.addEventListener('focusin', function () { c.focus = true; });
    root1.addEventListener('focusout', function (e) { if (!e.relatedTarget || !root1.contains(e.relatedTarget)) { c.focus = false; } });
    track.addEventListener('scroll', function () { carSchedule(c); }, { passive: true });
    if (typeof ResizeObserver === 'function') { new ResizeObserver(function () { carLayout(c); }).observe(track); }
    else { window.addEventListener('resize', function () { carLayout(c); }); }

    carLayout(c);
    c.ready = true;
    // a show that moves by itself starts for those who have not asked for less motion; the others can start it with the button
    if (c.ms) { if (reducedMotion()) { carToggleButton(c); } else { carStart(c); } }
  }

  function initCarousels(scope) { $$('[data-carousel]', scope).forEach(carInit); }

  // ------------------------------------------------------------------------------------------------------- init
  // a meter is filled by data: <div class="mat-meter__fill" data-value="63"> (no inline style in the markup)
  function fillMeters(scope) {
    $$(".mat-meter__fill[data-value]", scope).forEach(function (m) { m.style.setProperty("--mat-meter", Math.max(0, Math.min(100, parseFloat(m.getAttribute("data-value")) || 0)) + "%"); });
  }

  function init() {
    scheduleSpy();
    initForms();
    initPickers();
    initCarousels();
    // the saved state of the submenus is applied without animating them
    root.classList.add('mat-nav-init');
    restoreBranches();
    window.requestAnimationFrame(function () { window.requestAnimationFrame(function () { root.classList.remove('mat-nav-init'); }); });
    fillMeters();
    applyTheme(currentTheme(), false);
    refreshDependents();
    var custom = root.getAttribute('data-custom-accent');
    if (custom) { customAccent(custom); }
    $$('[data-toast]').forEach(function (el) { toast(el.getAttribute('data-toast'), { tone: el.getAttribute('data-tone') || undefined }); el.remove(); });
    $$('[data-view-key]').forEach(function (b) {
      var saved = null;
      try { saved = localStorage.getItem('mat-view-' + b.getAttribute('data-view-key')); } catch (e) { /* ignore */ }
      if (saved && b.getAttribute('data-view-switch') === saved) { setView(b); }
    });
    $$('dialog[data-autoopen]').forEach(function (d) { openDialog(d); });
    if (!doc.querySelector('.mat-backdrop') && app()) {
      var bd = doc.createElement('div'); bd.className = 'mat-backdrop'; bd.setAttribute('data-nav-close', ''); app().appendChild(bd);
    }
  }

  if (doc.readyState === 'loading') { doc.addEventListener('DOMContentLoaded', init); } else { init(); }

  window.MatUi = {
    version: '1',
    dialog: { open: openDialog, close: closeDialog },
    confirm: confirmDialog,
    alert: alertDialog,
    prompt: promptDialog,
    toast: toast,
    theme: { get: currentTheme, set: function (t) { applyTheme(t); } },
    accent: { set: setAccent, custom: customAccent },
    scrollspy: { update: updateSpy },
    nav: { set: setNavSet, branch: function (key, open) { var b = $('[data-nav-branch="' + key + '"]'); if (b) { setBranch(b, open !== false, true); } } },
    picker: { init: initPickers, close: closePicker },
    carousel: { init: initCarousels, go: function (el, page) { var c = el && el._matCarousel; if (c) { carGo(c, page, false); } } },
    refresh: function (scope) { refreshDependents(scope); fillMeters(scope); initForms(scope); initPickers(scope); initCarousels(scope); }
  };
})();
