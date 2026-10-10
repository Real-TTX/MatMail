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

  App.icon = function (name, extraClass) {
    var span = doc.createElement("span");
    span.className = "ico" + (extraClass ? " " + extraClass : "");
    span.innerHTML = '<svg class="icon" aria-hidden="true"><use href="#i-' + name + '"/></svg>';
    return span;
  };

  App.iconButton = function (name, label, onClick, extraClass) {
    var button = App.el("button", { type: "button", class: "icon-btn" + (extraClass ? " " + extraClass : ""), title: label, "aria-label": label });
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

  var colors = ["#1a73e8", "#d93025", "#188038", "#e37400", "#9334e6", "#007b83", "#c5221f", "#5f6368", "#a142f4", "#12b5cb"];
  App.avatar = function (name, address) {
    var label = (App.displayName(name, address) || "?").trim();
    var letter = (label.match(/[A-Za-zÀ-ÿ0-9]/) || ["?"])[0].toUpperCase();
    var hash = 0, source = (address || label).toLowerCase();
    for (var i = 0; i < source.length; i++) { hash = (hash * 31 + source.charCodeAt(i)) >>> 0; }
    var node = App.el("span", { class: "avatar avatar--round", "aria-hidden": "true", text: letter });
    node.style.background = colors[hash % colors.length];
    node.style.color = "#fff";
    return node;
  };

  // ---- Toasts --------------------------------------------------------------------------------
  App.toast = function (message, options) {
    options = options || {};
    var host = doc.getElementById("mail-toasts");
    var toast = App.el("div", { class: "toast" + (options.error ? " toast--error" : "") }, [App.el("span", { class: "toast__text", text: message })]);
    if (options.action) {
      var action = App.el("button", { type: "button", class: "toast__action", text: options.action.label });
      action.addEventListener("click", function () { options.action.run(); close(); });
      toast.appendChild(action);
    }
    var close = function () { toast.classList.add("is-leaving"); setTimeout(function () { toast.remove(); }, 200); };
    toast.appendChild(App.iconButton("x", "×", close, "toast__close"));
    host.appendChild(toast);
    setTimeout(close, options.duration || (options.error ? 8000 : 5000));
  };

  // ---- Menus (dropdowns; full-screen sheets on phones) ---------------------------------------
  var openMenu = null;
  App.closeMenu = function () {
    if (openMenu) { openMenu.node.remove(); openMenu = null; }
    var layer = doc.getElementById("mail-menu-layer");
    if (layer) { layer.hidden = true; layer.innerHTML = ""; }
  };

  // items: [{ label, icon, onClick, danger, disabled, divider, checked }]
  App.showMenu = function (anchor, items, options) {
    options = options || {};
    App.closeMenu();
    var layer = doc.getElementById("mail-menu-layer");
    var menu = App.el("div", { class: "menu", role: "menu" });
    var header = App.el("div", { class: "menu__header" }, [App.el("span", { class: "menu__title", text: options.title || "" }), App.iconButton("x", "×", App.closeMenu)]);
    menu.appendChild(header);
    items.forEach(function (item) {
      if (item.divider) { menu.appendChild(App.el("div", { class: "menu__divider" })); return; }
      var entry = App.el("button", { type: "button", class: "menu__item" + (item.danger ? " is-danger" : "") + (item.checked ? " is-checked" : ""), role: "menuitem", disabled: !!item.disabled });
      entry.appendChild(item.icon ? App.icon(item.icon) : App.el("span", { class: "ico" }));
      entry.appendChild(App.el("span", { class: "menu__label", text: item.label, style: item.indent ? "padding-left:" + (item.indent * 14) + "px" : null }));
      if (item.checked) { entry.appendChild(App.icon("check", "menu__check")); }
      entry.addEventListener("click", function () { App.closeMenu(); if (item.onClick) { item.onClick(); } });
      menu.appendChild(entry);
    });
    layer.appendChild(menu);
    layer.hidden = false;
    openMenu = { node: menu };

    // Desktop: place under the anchor (flip when there is no room); phones: CSS makes it a full-screen sheet.
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
    if (openMenu && !e.target.closest(".menu")) { App.closeMenu(); }
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

  // ---- Dialogs (the shared <dialog> styles; full screen on phones) ---------------------------
  App.confirm = function (message) {
    return new Promise(function (resolve) {
      var dialog = doc.getElementById("mm-confirm");
      dialog.querySelector(".confirm__text").textContent = message;
      dialog.returnValue = "";
      var done = function () { dialog.removeEventListener("close", done); resolve(dialog.returnValue === "ok"); };
      dialog.addEventListener("close", done);
      dialog.showModal();
    });
  };

  /** A dialog that only shows something: the Close button, Escape and a click on the backdrop close it. */
  App.infoDialog = function (title, subtitle, body) {
    var dialog = App.el("dialog", { class: "dialog dialog--sm" }, [
      App.el("div", { class: "dialog__header" }, [
        App.el("div", null, [App.el("h2", { class: "dialog__title", text: title }), subtitle ? App.el("div", { class: "dialog__subtitle muted", text: subtitle }) : null]),
        App.iconButton("x", App.T("close"), function () { dialog.close(); })
      ]),
      App.el("div", { class: "dialog__body" }, [body]),
      App.el("div", { class: "dialog__footer" }, [App.el("form", { method: "dialog" }, [App.el("button", { type: "submit", class: "btn btn--secondary", text: App.T("close") })])])
    ]);
    doc.body.appendChild(dialog);
    dialog.addEventListener("close", function () { dialog.remove(); });
    dialog.addEventListener("click", function (e) { if (e.target === dialog) { dialog.close(); } });
    dialog.showModal();
    return dialog;
  };

  App.promptDialog = function (title, value, label) {
    return new Promise(function (resolve) {
      var input = App.el("input", { type: "text", class: "form-control", value: value || "", "aria-label": label || title });
      var form = App.el("form", { method: "dialog", class: "form" }, [
        App.el("div", { class: "form-row" }, [App.el("label", { text: label || title }), input]),
        App.el("div", { class: "btn-row" }, [
          App.el("button", { type: "submit", class: "btn btn--primary", value: "ok", text: App.T("ok") }),
          App.el("button", { type: "submit", class: "btn btn--secondary", value: "cancel", formnovalidate: true, text: App.T("cancel") })
        ])
      ]);
      var dialog = App.el("dialog", { class: "dialog dialog--sm" }, [
        App.el("div", { class: "dialog__header" }, [App.el("h2", { class: "dialog__title", text: title })]),
        App.el("div", { class: "dialog__body" }, [form])
      ]);
      doc.body.appendChild(dialog);
      dialog.addEventListener("close", function () {
        var ok = dialog.returnValue === "ok";
        var result = ok ? input.value.trim() : null;
        dialog.remove();
        resolve(result);
      });
      dialog.showModal();
      input.focus();
      input.select();
    });
  };
})();
