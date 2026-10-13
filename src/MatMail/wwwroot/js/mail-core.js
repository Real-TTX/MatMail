// MatMail web client – core: API access, formatting helpers, shared state and small UI utilities (toasts, menus).
// The other mail-*.js files build on window.MailApp.
(function () {
  "use strict";

  var doc = document;

  var App = (window.MailApp = {
    boot: null,                 // /api/mail/bootstrap
    state: {
      mailboxId: 0, folderId: 0, query: "", page: 1, messageId: 0, previewId: "", preview: null,
      items: [], total: 0, pageSize: 50, selected: new Set(), allMatching: false, cursor: -1, loading: false
    },
    handlers: {}                // filled by mail-ui.js / mail-compose.js
  });

  // ---- Texts (handed over by the page as JSON) ----------------------------------------------
  var i18n = {};
  try { i18n = JSON.parse(doc.getElementById("mail-i18n").textContent); } catch (e) { /* keys are shown instead */ }
  App.T = function (key) { return i18n[key] !== undefined ? i18n[key] : key; };
  App.labels = { Inbox: App.T("inbox"), Drafts: App.T("drafts"), Sent: App.T("sent"), Archive: App.T("archive"), Junk: App.T("spam"), Trash: App.T("trash") };

  // ---- API -----------------------------------------------------------------------------------
  function csrf() {
    var meta = doc.querySelector('meta[name="csrf-token"]');
    return meta ? meta.getAttribute("content") : "";
  }

  App.api = function (method, url, body, options) {
    options = options || {};
    var headers = { "Accept": "application/json", "X-CSRF-TOKEN": csrf() };
    var init = { method: method, headers: headers, credentials: "same-origin" };
    if (body instanceof FormData) {
      init.body = body;
    } else if (body instanceof Blob) {
      // A file as it is (the raw body of the request): its name travels in a header.
      init.body = body;
      headers["Content-Type"] = "application/octet-stream";
      Object.keys(options.headers || {}).forEach(function (name) { headers[name] = options.headers[name]; });
    } else if (body !== undefined && body !== null) {
      headers["Content-Type"] = "application/json";
      init.body = JSON.stringify(body);
    }
    return fetch(url, init).then(function (response) {
      if (response.status === 401) { window.location.href = "/Account/Login?returnUrl=" + encodeURIComponent("/Mail"); throw new Error("unauthorized"); }
      var type = response.headers.get("content-type") || "";
      var parse = type.indexOf("json") >= 0 ? response.json() : Promise.resolve(null);
      return parse.then(function (data) {
        if (!response.ok) {
          var error = new Error((data && (data.error || data.title)) || ("HTTP " + response.status));
          error.status = response.status;
          error.data = data;
          throw error;
        }
        return data;
      });
    });
  };
  App.get = function (url) { return App.api("GET", url); };
  App.post = function (url, body) { return App.api("POST", url, body === undefined ? {} : body); };

  // ---- Helpers -------------------------------------------------------------------------------
  App.el = function (tag, attrs, children) {
    var node = doc.createElement(tag);
    if (attrs) {
      Object.keys(attrs).forEach(function (key) {
        var value = attrs[key];
        if (value === false || value === null || value === undefined) { return; }
        if (key === "class") { node.className = value; }
        else if (key === "text") { node.textContent = value; }
        else if (key === "html") { node.innerHTML = value; }
        else if (key.indexOf("on") === 0 && typeof value === "function") { node.addEventListener(key.slice(2), value); }
        else if (value === true) { node.setAttribute(key, ""); }
        else { node.setAttribute(key, value); }
      });
    }
    (children || []).forEach(function (child) {
      if (child === null || child === undefined || child === false) { return; }
      node.appendChild(typeof child === "string" ? doc.createTextNode(child) : child);
    });
    return node;
  };

  // The two sprites of the page (matui-icons.svg: mat-i-NAME, the icons of MatMail's own library: mat-il-NAME).
  var symbols = {};
  App.symbol = function (name) {
    if (!symbols[name]) { symbols[name] = doc.getElementById("mat-i-" + name) ? "#mat-i-" + name : "#mat-il-" + name; }
    return symbols[name];
  };

  /** The icon itself, an svg of the toolkit (for places where the toolkit styles the svg as a direct child, like alerts and toasts). */
  App.svg = function (name, extraClass) {
    var holder = doc.createElement("span");
    holder.innerHTML = '<svg class="mat-icon' + (extraClass ? " " + extraClass : "") + '" aria-hidden="true" focusable="false"><use href="' + App.symbol(name) + '"/></svg>';
    return holder.firstChild;
  };

  /** The icon in a small wrapper (the style sheet of the client aligns it); extraClass goes to the wrapper. */
  App.icon = function (name, extraClass) {
    var span = doc.createElement("span");
    span.className = "ico" + (extraClass ? " " + extraClass : "");
    span.appendChild(App.svg(name));
    return span;
  };

  /** An icon that is a button: the label is its name and, on a pointer device, its tooltip (BUTTON-04). */
  App.iconButton = function (name, label, onClick, extraClass) {
    var button = App.el("button", { type: "button", class: "mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm" + (extraClass ? " " + extraClass : ""), "data-tip": label, "aria-label": label });
    button.appendChild(App.icon(name));
    if (onClick) { button.addEventListener("click", onClick); }
    return button;
  };

  App.esc = function (value) {
    return String(value === null || value === undefined ? "" : value).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  };

  App.debounce = function (fn, wait) {
    var timer;
    return function () {
      var args = arguments, self = this;
      clearTimeout(timer);
      timer = setTimeout(function () { fn.apply(self, args); }, wait);
    };
  };

  App.formatSize = function (bytes) {
    var units = ["B", "KB", "MB", "GB"], value = bytes, unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return (unit === 0 ? value : value.toFixed(value >= 100 ? 0 : 1)) + " " + units[unit];
  };

  var locale = (doc.documentElement.lang || "en");
  // The time zone the user chose in their appearance settings; without one the browser's own is used.
  var zone = doc.documentElement.getAttribute("data-time-zone") || undefined;
  try { new Intl.DateTimeFormat(locale, { timeZone: zone }); } catch (e) { zone = undefined; }   // a zone this browser does not know: use its own
  function dayOf(date) { return date.toLocaleDateString("en-CA", { timeZone: zone }); }   // yyyy-mm-dd in that zone
  App.formatListDate = function (iso) {
    var d = new Date(iso), now = new Date();
    if (dayOf(d) === dayOf(now)) { return d.toLocaleTimeString(locale, { hour: "2-digit", minute: "2-digit", timeZone: zone }); }
    if (dayOf(d).slice(0, 4) === dayOf(now).slice(0, 4)) { return d.toLocaleDateString(locale, { day: "numeric", month: "short", timeZone: zone }); }
    return d.toLocaleDateString(locale, { year: "numeric", month: "numeric", day: "numeric", timeZone: zone });
  };
  App.formatFullDate = function (iso) {
    return new Date(iso).toLocaleString(locale, { weekday: "short", year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit", timeZone: zone });
  };

  App.displayName = function (name, address) { return name && name.trim() ? name : (address || ""); };

  /** The first letter of the name in a flat accent circle (the avatar of the toolkit): no colour per sender, no inline style. */
  App.avatar = function (name, address) {
    var label = (App.displayName(name, address) || "?").trim();
    var letter = (label.match(/[A-Za-zÀ-ÿ0-9]/) || ["?"])[0].toUpperCase();
    return App.el("span", { class: "mat-avatar mail-avatar", "aria-hidden": "true", text: letter });
  };

  // ---- Toasts (the host and the look are the toolkit's; one with a button, like "Undo", is built here) ----------------------------
  App.toast = function (message, options) {
    options = options || {};
    var host = doc.querySelector(".mat-toasts");
    if (!host) { host = App.el("div", { class: "mat-toasts", role: "status", "aria-live": "polite" }); doc.body.appendChild(host); }
    var toast = App.el("div", { class: "mat-toast mat-toast--" + (options.error ? "danger" : "info") });
    toast.appendChild(App.svg(options.error ? "x-circle" : "info"));
    toast.appendChild(App.el("div", { class: "mat-alert__body", text: message }));
    var close = function () { toast.remove(); };
    if (options.action) {
      var action = App.el("button", { type: "button", class: "mat-btn mat-btn--ghost mat-btn--sm", text: options.action.label });
      action.addEventListener("click", function () { options.action.run(); close(); });
      toast.appendChild(action);
    }

    var x = App.el("button", { type: "button", class: "mat-alert__close", "aria-label": App.T("close") });
    x.appendChild(App.svg("x"));
    x.addEventListener("click", close);
    toast.appendChild(x);
    host.appendChild(toast);
    setTimeout(close, options.duration || (options.error ? 9000 : 6000));
  };

  // ---- Menus (dropdowns; full-screen sheets on phones) ---------------------------------------
  var openMenu = null;
  App.closeMenu = function () {
    if (openMenu) { openMenu.node.remove(); openMenu = null; }
    var layer = doc.getElementById("mail-menu-layer");
    if (layer) { layer.hidden = true; layer.innerHTML = ""; }
  };

  // items: [{ label, icon, onClick, danger, disabled, divider, checked, indent }]
  App.showMenu = function (anchor, items, options) {
    options = options || {};
    App.closeMenu();
    var layer = doc.getElementById("mail-menu-layer");
    var menu = App.el("div", { class: "mat-menu mail-menu", role: "menu" });
    var header = App.el("div", { class: "mail-menu__header" }, [App.el("span", { class: "mail-menu__title", text: options.title || "" }), App.iconButton("x", App.T("close"), App.closeMenu)]);
    menu.appendChild(header);
    items.forEach(function (item) {
      if (item.divider) { menu.appendChild(App.el("div", { class: "mat-menu__sep", role: "separator" })); return; }
      var entry = App.el("button", { type: "button", class: "mat-menu__item" + (item.danger ? " mat-menu__item--danger" : ""), role: item.checked !== undefined ? "menuitemradio" : "menuitem", "aria-checked": item.checked !== undefined ? String(!!item.checked) : null, disabled: !!item.disabled });
      entry.appendChild(item.icon ? App.icon(item.icon) : App.el("span", { class: "ico" }));
      var label = App.el("span", { class: "mail-menu__label", text: item.label });
      if (item.indent) { label.style.setProperty("--mm-indent", String(item.indent)); }
      entry.appendChild(label);
      if (item.checked) { entry.appendChild(App.icon("check", "mat-menu__check")); }
      entry.addEventListener("click", function () { App.closeMenu(); if (item.onClick) { item.onClick(); } });
      menu.appendChild(entry);
    });
    layer.appendChild(menu);
    layer.hidden = false;
    openMenu = { node: menu };

    // Desktop: place under the anchor (flip when there is no room); phones: the style sheet makes it a full-screen sheet (DIALOG-07).
    if (window.matchMedia("(min-width: 721px)").matches && anchor) {
      var rect = anchor.getBoundingClientRect();
      var width = menu.offsetWidth, height = menu.offsetHeight;
      var left = options.alignRight ? rect.right - width : rect.left;
      left = Math.max(8, Math.min(left, window.innerWidth - width - 8));
      var top = rect.bottom + 4;
      if (top + height > window.innerHeight - 8) { top = Math.max(8, rect.top - height - 4); }
      menu.style.left = left + "px";
      menu.style.top = top + "px";
    }
  };

  doc.addEventListener("mousedown", function (e) {
    if (openMenu && !e.target.closest(".mail-menu")) { App.closeMenu(); }
  }, true);
  doc.addEventListener("keydown", function (e) { if (e.key === "Escape") { App.closeMenu(); } });
  window.addEventListener("resize", function () { App.closeMenu(); });

  // ---- Lookups -------------------------------------------------------------------------------
  App.mailbox = function (id) { return (App.boot.mailboxes || []).filter(function (m) { return m.id === id; })[0]; };
  App.folder = function (id) {
    var found = null;
    (App.boot.mailboxes || []).forEach(function (m) { m.folders.forEach(function (f) { if (f.id === id) { found = { mailbox: m, folder: f }; } }); });
    return found;
  };
  App.folderByKind = function (mailboxId, kind) {
    var box = App.mailbox(mailboxId);
    return box ? box.folders.filter(function (f) { return f.kind === kind; })[0] : null;
  };

  App.applyCounts = function (counts) {
    if (!counts) { return; }
    Object.keys(counts).forEach(function (id) {
      var hit = App.folder(parseInt(id, 10));
      if (hit) { hit.folder.unread = counts[id].unread; hit.folder.total = counts[id].total; }
    });
    if (App.handlers.renderFolders) { App.handlers.renderFolders(); }
  };

  App.folderLabel = function (folder) { return App.labels[folder.kind] || folder.name; };

  // ---- Dialogs: the ones of the toolkit (MatUi), full screen on phones ----------------------------------------------------------
  /** Asks first; resolves with true when the person agrees. danger: the confirming button is the red one, Cancel comes first (DIALOG-06). */
  App.confirm = function (message, options) {
    options = options || {};
    return window.MatUi.confirm(message, { danger: !!options.danger, ok: options.ok });
  };

  /** A dialog that only shows something: the Close button, Escape and a click outside close it. body: a node. */
  App.infoDialog = function (title, subtitle, body) {
    var dialog = App.el("dialog", { class: "mat-dialog mat-dialog--sm", "aria-label": title }, [
      App.el("div", { class: "mat-dialog__form" }, [
        App.el("div", { class: "mat-dialog__head" }, [
          App.el("div", { class: "mail-dialog__title" }, [App.el("h2", { class: "mat-dialog__title", text: title }), subtitle ? App.el("small", { class: "mat-muted", text: subtitle }) : null]),
          App.el("button", { type: "button", class: "mat-btn mat-btn--ghost mat-btn--icon mat-dialog__close", "data-dialog-close": "", "aria-label": App.T("close") }, [App.icon("x")])
        ]),
        App.el("div", { class: "mat-dialog__body" }, [body]),
        App.el("div", { class: "mat-dialog__foot" }, [App.el("div", { class: "mat-btn-row mat-btn-row--plain" }, [App.el("button", { type: "button", class: "mat-btn mat-btn--secondary", "data-dialog-close": "", text: App.T("close") })])])
      ])
    ]);
    doc.body.appendChild(dialog);
    dialog.addEventListener("close", function () { dialog.remove(); });
    window.MatUi.dialog.open(dialog);
    return dialog;
  };

  /** Asks for one line of text; resolves with the trimmed text, or null when the person cancels. */
  App.promptDialog = function (title, value, label) {
    return window.MatUi.prompt(label || title, { title: title, value: value || "", ok: App.T("ok"), cancel: App.T("cancel") }).then(function (text) {
      return text === null || text === undefined ? null : String(text).trim();
    });
  };
})();
