// MatMail – shared behaviour of the controls: sidebar, dropdowns, dialogs, confirm, tabs, toolbar, dependent fields, theme.
// No dependencies. Every control is found by its data attribute, so any number of instances can share a page.
(function () {
  "use strict";

  var doc = document;

  function ready(fn) {
    if (doc.readyState === "loading") { doc.addEventListener("DOMContentLoaded", fn); } else { fn(); }
  }

  function $(selector, root) { return (root || doc).querySelector(selector); }
  function $$(selector, root) { return Array.prototype.slice.call((root || doc).querySelectorAll(selector)); }

  // ---- Sidebar (phones: off-canvas) ----------------------------------------------------------
  function initSidebar() {
    var sidebar = $("#sidebar"), toggle = $("#sidebar-toggle"), backdrop = $("#sidebar-backdrop"), close = $("#sidebar-close");
    if (!sidebar) { return; }
    function setOpen(open) {
      sidebar.classList.toggle("is-open", open);
      if (backdrop) { backdrop.hidden = !open; }
      if (toggle) { toggle.setAttribute("aria-expanded", open ? "true" : "false"); }
    }
    if (toggle) { toggle.addEventListener("click", function () { setOpen(!sidebar.classList.contains("is-open")); }); }
    if (backdrop) { backdrop.addEventListener("click", function () { setOpen(false); }); }
    if (close) { close.addEventListener("click", function () { setOpen(false); }); }
    sidebar.addEventListener("click", function (e) {
      if (e.target.closest("a.nav-item, a.area-link") && window.matchMedia("(max-width: 960px)").matches) { setOpen(false); }
    });
    window.MatMail = window.MatMail || {};
    window.MatMail.setSidebarOpen = setOpen;
  }

  // ---- Dropdown menus ------------------------------------------------------------------------
  function initDropdowns() {
    function closeAll(except) {
      $$("[data-dropdown].is-open").forEach(function (d) {
        if (d !== except) { setOpen(d, false); }
      });
    }
    function setOpen(dropdown, open) {
      var menu = $(".dropdown__menu", dropdown), toggle = $("[data-dropdown-toggle]", dropdown);
      if (!menu) { return; }
      menu.hidden = !open;
      dropdown.classList.toggle("is-open", open);
      if (toggle) { toggle.setAttribute("aria-expanded", open ? "true" : "false"); }
    }
    doc.addEventListener("click", function (e) {
      var toggle = e.target.closest("[data-dropdown-toggle]");
      if (toggle) {
        var dropdown = toggle.closest("[data-dropdown]");
        var willOpen = !dropdown.classList.contains("is-open");
        closeAll(dropdown);
        setOpen(dropdown, willOpen);
        return;
      }
      var closer = e.target.closest("[data-dropdown-close]");
      if (closer) { closeAll(null); return; }
      var insideMenu = e.target.closest(".dropdown__menu");
      if (insideMenu && e.target.closest("a, button[type=submit], [data-dropdown-pick]")) {
        // Choosing an entry closes the menu (forms and links navigate away anyway).
        var owner = insideMenu.closest("[data-dropdown]");
        if (owner && !e.target.closest("[data-keep-open]")) { setOpen(owner, false); }
        return;
      }
      if (!insideMenu) { closeAll(null); }
    });
    doc.addEventListener("keydown", function (e) { if (e.key === "Escape") { closeAll(null); } });
  }

  // ---- Dialogs -------------------------------------------------------------------------------
  function initDialogs() {
    doc.addEventListener("click", function (e) {
      var opener = e.target.closest("[data-dialog-open]");
      if (opener) {
        var dialog = doc.getElementById(opener.getAttribute("data-dialog-open"));
        if (dialog && dialog.showModal) { e.preventDefault(); dialog.showModal(); }
        return;
      }
      var closer = e.target.closest("[data-dialog-close]");
      if (closer) {
        var host = closer.closest("dialog");
        if (host) { host.close(); }
        return;
      }
      if (e.target.tagName === "DIALOG" && e.target.classList.contains("dialog") && !e.target.classList.contains("confirm")) {
        // A click on the backdrop closes the dialog.
        var rect = e.target.getBoundingClientRect();
        var inside = e.clientX >= rect.left && e.clientX <= rect.right && e.clientY >= rect.top && e.clientY <= rect.bottom;
        if (!inside) { e.target.close(); }
      }
    });
  }

  // ---- Confirmation for destructive actions --------------------------------------------------
  function initConfirm() {
    var dialog = doc.getElementById("mm-confirm");
    if (!dialog || !dialog.showModal) { return; }
    var text = $(".confirm__text", dialog);
    var pending = null;

    doc.addEventListener("click", function (e) {
      var button = e.target.closest("[data-confirm]");
      if (!button || button.dataset.confirmed === "1") { return; }
      e.preventDefault();
      e.stopPropagation();
      pending = button;
      text.textContent = button.getAttribute("data-confirm");
      dialog.showModal();
    }, true);

    dialog.addEventListener("close", function () {
      var button = pending;
      pending = null;
      if (dialog.returnValue !== "ok" || !button) { return; }
      button.dataset.confirmed = "1";
      if (button.form && button.type === "submit") {
        if (button.form.requestSubmit) { button.form.requestSubmit(button); } else { button.click(); }
      } else {
        button.click();
      }
      setTimeout(function () { delete button.dataset.confirmed; }, 0);
    });
  }

  // ---- Panel tabs ----------------------------------------------------------------------------
  function initTabs() {
    $$("[data-tabs]").forEach(function (root) {
      var tabs = $$("[data-tab]", root).filter(function (t) { return t.closest("[data-tabs]") === root; });
      var panels = tabs.map(function (t) { return doc.getElementById(t.getAttribute("data-tab")); });
      function activate(id) {
        tabs.forEach(function (t, i) {
          var on = t.getAttribute("data-tab") === id;
          t.classList.toggle("is-active", on);
          t.setAttribute("aria-selected", on ? "true" : "false");
          if (panels[i]) { panels[i].hidden = !on; }
        });
      }
      tabs.forEach(function (t) { t.addEventListener("click", function () { activate(t.getAttribute("data-tab")); }); });

      // A tab with a validation error opens by itself; otherwise the first one if none is open.
      var broken = panels.findIndex(function (p) { return p && p.querySelector(".input-validation-error, .field-error:not(:empty)"); });
      if (broken >= 0) { activate(tabs[broken].getAttribute("data-tab")); }
      else if (!tabs.some(function (t) { return t.classList.contains("is-active"); }) && tabs.length) { activate(tabs[0].getAttribute("data-tab")); }
    });
  }

  // ---- Toolbar: apply on change, folding filters on phones -------------------------------------
  function initToolbars() {
    $$("form.toolbar").forEach(function (form) {
      var timer;
      $$("[data-autosubmit]", form).forEach(function (el) {
        el.addEventListener("change", function () { form.submit(); });
      });
      var search = $("input[type=search]", form);
      if (search) {
        search.addEventListener("input", function () {
          clearTimeout(timer);
          timer = setTimeout(function () { form.submit(); }, 450);
        });
      }
      // Always return to the first page when the filter changes.
      form.addEventListener("submit", function () {
        $$("input[name=PageNumber]", form).forEach(function (i) { i.remove(); });
      });

      var filters = $$(".toolbar__filter", form);
      if (filters.length) {
        var toggle = doc.createElement("button");
        toggle.type = "button";
        toggle.className = "btn btn--secondary toolbar__toggle";
        toggle.innerHTML = '<svg class="icon" aria-hidden="true"><use href="#i-filter"/></svg><span></span>';
        $("span", toggle).textContent = form.getAttribute("data-t-filters") || "Filter";
        toggle.addEventListener("click", function () { form.classList.toggle("is-open"); });
        var searchGroup = $(".toolbar__search", form);
        if (searchGroup && searchGroup.nextSibling) { form.insertBefore(toggle, searchGroup.nextSibling); } else { form.appendChild(toggle); }
        // Filters that are active when the page opens keep the panel open.
        if (filters.some(function (f) { var s = $("select", f); return s && s.selectedIndex > 0; })) { form.classList.add("is-open"); }
      }
    });
  }

  // ---- Rows that open their detail page ------------------------------------------------------
  function initRowLinks() {
    doc.addEventListener("click", function (e) {
      var row = e.target.closest("tr[data-row-href]");
      if (!row || e.target.closest("a, button, input, select, label, form")) { return; }
      var selection = window.getSelection && window.getSelection().toString();
      if (selection) { return; }
      window.location.href = row.getAttribute("data-row-href");
    });
    doc.addEventListener("keydown", function (e) {
      if (e.key !== "Enter") { return; }
      var row = e.target.closest && e.target.closest("tr[data-row-href]");
      if (row && e.target === row) { window.location.href = row.getAttribute("data-row-href"); }
    });
  }

  // ---- Dependent fields ----------------------------------------------------------------------
  // <div data-show-when-field="Input.Role" data-show-when-value="Mail,Backup">: shown only while that input has one of the values.
  // Hidden rows are disabled so they neither post nor take part in validation.
  function initDependentFields() {
    var dependents = $$("[data-show-when-field]");
    if (!dependents.length) { return; }

    function valueOf(name) {
      var inputs = $$('[name="' + name + '"]').filter(function (i) { return !i.closest("[data-show-when-field][hidden]") || true; });
      if (!inputs.length) { return ""; }
      var first = inputs[0];
      if (first.type === "checkbox") { return first.checked ? "true" : "false"; }
      if (first.type === "radio") { var checked = inputs.find(function (i) { return i.checked; }); return checked ? checked.value : ""; }
      return first.value;
    }
    function apply() {
      dependents.forEach(function (row) {
        var field = row.getAttribute("data-show-when-field");
        var wanted = (row.getAttribute("data-show-when-value") || "").split(",").map(function (v) { return v.trim().toLowerCase(); });
        var parentHidden = row.parentElement && row.parentElement.closest("[data-show-when-field][hidden]");
        var current = String(valueOf(field)).toLowerCase();
        var show = !parentHidden && wanted.indexOf(current) >= 0;
        row.hidden = !show;
        $$("input, select, textarea, button", row).forEach(function (el) {
          if (el.closest("[data-keep-enabled]")) { return; }
          el.disabled = !show;
        });
      });
    }
    doc.addEventListener("change", function (e) { if (e.target && e.target.name) { apply(); } });
    doc.addEventListener("input", function (e) { if (e.target && e.target.name) { apply(); } });
    apply();
    // Picker widgets write their hidden inputs programmatically and announce it with a bubbling "change".
  }

  // ---- Password reveal -----------------------------------------------------------------------
  function initReveal() {
    doc.addEventListener("click", function (e) {
      var button = e.target.closest("[data-reveal]");
      if (!button) { return; }
      var input = $("input", button.parentElement);
      if (!input) { return; }
      var show = input.type === "password";
      input.type = show ? "text" : "password";
      var use = $("use", button);
      if (use) { use.setAttribute("href", show ? "#i-eye-off" : "#i-eye"); }
    });
  }

  // ---- Copy to clipboard ---------------------------------------------------------------------
  function initCopy() {
    doc.addEventListener("click", function (e) {
      var button = e.target.closest("[data-copy]");
      if (!button) { return; }
      var value = button.getAttribute("data-copy");
      if (navigator.clipboard) { navigator.clipboard.writeText(value); }
      button.classList.add("is-done");
      setTimeout(function () { button.classList.remove("is-done"); }, 1200);
    });
  }

  // ---- Prevent double submits ----------------------------------------------------------------
  function initBusy() {
    doc.addEventListener("submit", function (e) {
      var form = e.target;
      if (!(form instanceof HTMLFormElement) || form.hasAttribute("data-no-busy") || form.classList.contains("toolbar") || form.method === "dialog") { return; }
      if (e.defaultPrevented) { return; }
      var submitter = e.submitter;
      setTimeout(function () {
        if (submitter && !submitter.disabled) { submitter.classList.add("is-busy"); }
      }, 0);
      // Re-enable when the page comes back from the history (back button).
      window.addEventListener("pageshow", function () { if (submitter) { submitter.classList.remove("is-busy"); } }, { once: true });
    });
  }

  // ---- Theme (system / light / dark) ---------------------------------------------------------
  function initTheme() {
    var root = doc.documentElement;
    function apply(mode) {
      var dark = mode === "dark" || (mode === "system" && window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches);
      root.setAttribute("data-theme-mode", mode);
      root.setAttribute("data-mode", dark ? "dark" : "light");
      if (window.matmailBar) { window.matmailBar(); }   // the bar of the browser follows (see _ThemeHead)
    }
    $$("[data-theme-switch]").forEach(function (group) {
      group.addEventListener("click", function (e) {
        var item = e.target.closest("[data-theme-mode]");
        if (!item) { return; }
        var mode = item.getAttribute("data-theme-mode");
        $$("[data-theme-mode]", group).forEach(function (i) { i.classList.toggle("is-active", i === item); });
        apply(mode);
        try { localStorage.setItem("matmail-theme", mode); } catch (err) { /* storage unavailable */ }
        var url = group.getAttribute("data-save-url");
        var token = $('input[name="__RequestVerificationToken"]');
        if (url && token) {
          var body = new URLSearchParams();
          body.set("mode", mode);
          body.set("__RequestVerificationToken", token.value);
          fetch(url, { method: "POST", body: body, credentials: "same-origin" }).catch(function () { /* the next page load shows the saved state */ });
        }
      });
    });
    if (window.matchMedia) {
      var query = window.matchMedia("(prefers-color-scheme: dark)");
      var listener = function () { if (root.getAttribute("data-theme-mode") === "system") { apply("system"); } };
      if (query.addEventListener) { query.addEventListener("change", listener); }
    }
  }

  // ---- "Synchronise now": fetches the connected accounts that feed the user's mailboxes -------------
  // <button data-sync-now data-t-…="texts">: asks the server, tells what came in and lets the mail client refresh itself.
  function toast(message, error) {
    var host = doc.getElementById("mail-toasts");
    if (!host) {
      host = doc.createElement("div");
      host.id = "mail-toasts";
      host.className = "mail-toasts";
      host.setAttribute("role", "status");
      doc.body.appendChild(host);
    }
    var node = doc.createElement("div");
    node.className = "toast" + (error ? " toast--error" : "");
    var text = doc.createElement("span");
    text.className = "toast__text";
    text.textContent = message;
    node.appendChild(text);
    host.appendChild(node);
    setTimeout(function () { node.classList.add("is-leaving"); setTimeout(function () { node.remove(); }, 200); }, error ? 9000 : 6000);
  }

  function initSyncNow() {
    doc.addEventListener("click", function (e) {
      var button = e.target.closest("[data-sync-now]");
      if (!button || button.disabled) { return; }
      var t = function (key) { return button.getAttribute("data-t-" + key) || ""; };
      var label = $(".sync-now__label", button);
      var original = label ? label.textContent : "";
      button.disabled = true;
      button.classList.add("is-busy");
      if (label) { label.textContent = t("busy") || original; }

      fetch("/api/mail/csrf", { credentials: "same-origin", headers: { "Accept": "application/json" } })
        .then(function (r) { return r.json(); })
        .then(function (csrf) {
          return fetch("/api/mail/sync", { method: "POST", credentials: "same-origin", headers: { "Accept": "application/json", "X-CSRF-TOKEN": csrf.token } });
        })
        .then(function (response) { return response.json().then(function (data) { return { ok: response.ok, data: data }; }); })
        .then(function (result) {
          if (!result.ok) { toast((result.data && result.data.error) || t("error"), true); return; }
          var r = result.data, parts = [];
          if (r.accounts === 0) { toast(t("none")); return; }
          if (r.synced > 0 || (r.failed === 0 && r.stillRunning === 0)) { parts.push(r.downloaded > 0 ? t("done").replace("{0}", r.downloaded) : t("nonew")); }
          if (r.stillRunning > 0) { parts.push(t("running").replace("{0}", r.stillRunning)); }
          if (r.failed > 0) { parts.push(t("failed").replace("{0}", r.failed) + (r.firstProblem ? " " + r.firstProblem : "")); }
          toast(parts.join(" "), r.failed > 0);
          window.dispatchEvent(new CustomEvent("matmail:synced", { detail: r }));
        })
        .catch(function () { toast(t("error"), true); })
        .then(function () {
          button.disabled = false;
          button.classList.remove("is-busy");
          if (label) { label.textContent = original; }
        });
    });
  }

  // ---- Auto-dismiss of success notices -------------------------------------------------------
  function initNotices() {
    $$(".notice--ok").forEach(function (n) { setTimeout(function () { n.style.transition = "opacity .4s"; n.style.opacity = "0"; setTimeout(function () { n.remove(); }, 450); }, 7000); });
  }

  ready(function () {
    initSidebar();
    initDropdowns();
    initDialogs();
    initConfirm();
    initTabs();
    initToolbars();
    initRowLinks();
    initDependentFields();
    initReveal();
    initCopy();
    initBusy();
    initTheme();
    initSyncNow();
    initNotices();
  });
})();
