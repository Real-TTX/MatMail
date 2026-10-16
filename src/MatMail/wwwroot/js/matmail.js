/* ==========================================================================
   MatMail · what the app adds to the toolkit
   matui.js does the menu, the dialogs, the tabs, the toasts, the forms, the pickers and the theme. What is left here is what only MatMail does:
   rows that open their page, a busy button after a submit, "Synchronise now", the labels of the theme button in the language of the page, the colour of
   the bar of the browser, and a tab that opens by itself when a field in it has an error.
   ========================================================================== */
(function () {
  "use strict";

  var doc = document;
  var root = doc.documentElement;

  function $(selector, context) { return (context || doc).querySelector(selector); }
  function $$(selector, context) { return Array.prototype.slice.call((context || doc).querySelectorAll(selector)); }
  function ready(fn) {
    if (doc.readyState === "loading") { doc.addEventListener("DOMContentLoaded", fn); } else { fn(); }
  }

  // ---- The menu drawer of a phone: the mail client closes it after a choice -----------------------
  window.MatMail = window.MatMail || {};
  window.MatMail.setSidebarOpen = function (open) {
    var shell = $(".mat-app");
    var close = $("[data-nav-close]");
    if (!open && shell && shell.classList.contains("is-nav-open") && close) { close.click(); }
  };

  // ---- Rows that open their detail page (the title in the row is the link; the row is a bigger target) ----------
  function initRowLinks() {
    doc.addEventListener("click", function (e) {
      var row = e.target.closest("tr[data-row-href], tr[data-row-submit]");
      if (!row || e.target.closest("a, button, input, select, label, form")) { return; }
      var selection = window.getSelection && window.getSelection().toString();
      if (selection) { return; }
      openRow(row);
    });
    doc.addEventListener("keydown", function (e) {
      if (e.key !== "Enter") { return; }
      var row = e.target.closest && e.target.closest("tr[data-row-href], tr[data-row-submit]");
      if (row && e.target === row) { openRow(row); }
    });
  }

  // A row opens its page (data-row-href) or, when opening has to change something first (the platform lists switch into the tenant
  // of the row), submits the form it holds (data-row-submit, form[data-row-form]).
  function openRow(row) {
    if (row.hasAttribute("data-row-submit")) {
      var form = row.querySelector("form[data-row-form]");
      if (form) { form.requestSubmit(); }
      return;
    }
    window.location.href = row.getAttribute("data-row-href");
  }

  // ---- A submit that is on its way: the button shows it and cannot be pressed twice -----------------
  function initBusy() {
    doc.addEventListener("submit", function (e) {
      var form = e.target;
      if (!(form instanceof HTMLFormElement) || form.hasAttribute("data-no-busy") || form.method === "dialog" || form.id.indexOf("-form") > 0) { return; }
      if (e.defaultPrevented) { return; }
      var submitter = e.submitter;
      setTimeout(function () {
        if (submitter && !submitter.disabled) { submitter.classList.add("is-loading"); }
      }, 0);
      // Back to normal when the page comes back from the history (the back button).
      window.addEventListener("pageshow", function () { if (submitter) { submitter.classList.remove("is-loading"); } }, { once: true });
    });
  }

  // ---- "Synchronise now": fetches the connected accounts that feed the user's mailboxes -------------------
  // <button data-sync-now data-t-…="texts">: asks the server, tells what came in and lets the mail client refresh itself.
  function initSyncNow() {
    doc.addEventListener("click", function (e) {
      var button = e.target.closest("[data-sync-now]");
      if (!button || button.disabled) { return; }
      var t = function (key) { return button.getAttribute("data-t-" + key) || ""; };
      var label = $(".mm-sync-label", button);
      var original = label ? label.textContent : "";
      var say = function (message, error) { if (window.MatUi) { window.MatUi.toast(message, { tone: error ? "danger" : "info", timeout: error ? 9000 : 6000 }); } };
      button.disabled = true;
      if (label) { label.textContent = t("busy") || original; }

      fetch("/api/mail/csrf", { credentials: "same-origin", headers: { "Accept": "application/json" } })
        .then(function (r) { return r.json(); })
        .then(function (csrf) {
          return fetch("/api/mail/sync", { method: "POST", credentials: "same-origin", headers: { "Accept": "application/json", "X-CSRF-TOKEN": csrf.token } });
        })
        .then(function (response) { return response.json().then(function (data) { return { ok: response.ok, data: data }; }); })
        .then(function (result) {
          if (!result.ok) { say((result.data && result.data.error) || t("error"), true); return; }
          var r = result.data, parts = [];
          if (r.accounts === 0) { say(t("none")); return; }
          if (r.synced > 0 || (r.failed === 0 && r.stillRunning === 0)) { parts.push(r.downloaded > 0 ? t("done").replace("{0}", r.downloaded) : t("nonew")); }
          if (r.stillRunning > 0) { parts.push(t("running").replace("{0}", r.stillRunning)); }
          if (r.failed > 0) { parts.push(t("failed").replace("{0}", r.failed) + (r.firstProblem ? " " + r.firstProblem : "")); }
          say(parts.join(" "), r.failed > 0);
          window.dispatchEvent(new CustomEvent("matmail:synced", { detail: r }));
        })
        .catch(function () { say(t("error"), true); })
        .then(function () {
          button.disabled = false;
          if (label) { label.textContent = original; }
        });
    });
  }

  // ---- The theme button names the state in the language of the page; the bar of the browser follows the theme -----------------------
  function labelThemeButton() {
    var state = root.getAttribute("data-theme");
    state = state === "light" || state === "dark" ? state : "system";
    var order = ["system", "light", "dark"];
    $$("[data-theme-toggle]").forEach(function (button) {
      var name = function (key) { return button.getAttribute("data-t-" + key) || key; };
      var next = order[(order.indexOf(state) + 1) % order.length];
      var text = (button.getAttribute("data-t-label") || "{0} → {1}").replace("{0}", name(state)).replace("{1}", name(next));
      button.setAttribute("aria-label", text);
      button.setAttribute("title", text);
    });
    var meta = $('meta[name="theme-color"]');
    var colour = getComputedStyle(root).getPropertyValue("--mat-glass").trim();
    if (meta && colour) { meta.setAttribute("content", colour); }
  }

  // ---- A choice that shows what it does before it is saved ----------------------------------------------
  // <div data-live="data-text-size"> with radios: the value of the checked one goes to that attribute of <html>, where the style sheet reacts to it.
  // "system" (theme) and "default" (accent) mean: no attribute.
  function initLiveChoices() {
    doc.addEventListener("change", function (e) {
      var input = e.target;
      var group = input && input.type === "radio" ? input.closest("[data-live]") : null;
      if (!group) { return; }
      var attribute = group.getAttribute("data-live");
      if (input.value === "system" || input.value === "default") { root.removeAttribute(attribute); } else { root.setAttribute(attribute, input.value); }
      if (attribute === "data-theme") { labelThemeButton(); }
    });
  }

  // ---- A field with an error in a tab that is closed: the tab opens (the toolkit's summary links to the field) -----------------------
  function openTabWithError() {
    var panel = $$('.mat-tabpanel[hidden]').filter(function (p) { return $(".is-invalid", p); })[0];
    if (!panel) { return; }
    var tab = $('[role="tab"][aria-controls="' + panel.id + '"]');
    if (tab) { tab.click(); }
  }

  ready(function () {
    initRowLinks();
    initBusy();
    initSyncNow();
    initLiveChoices();
    labelThemeButton();
    openTabWithError();
    root.addEventListener("mat:theme", labelThemeButton);
  });
})();
