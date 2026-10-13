// MatMail – the "App" page of the account: installing the app and turning notifications on and off for this device (see pwa.js).
(function () {
  "use strict";

  var doc = document;
  var pwa = window.MatMailPwa;
  var texts = {};
  try { texts = JSON.parse(doc.getElementById("app-i18n").textContent); } catch (e) { /* the keys stay empty */ }
  function T(key) { return texts[key] || ""; }
  function $(id) { return doc.getElementById(id); }
  if (!pwa) { return; }

  // ---- Install -----------------------------------------------------------------------------------
  var installText = $("app-install-text");
  var installButton = $("app-install-button");

  function showInstall() {
    if (pwa.install.isStandalone()) { installText.textContent = T("installed"); installButton.hidden = true; return; }
    if (pwa.install.available()) { installText.textContent = T("installHint"); installButton.hidden = false; return; }
    installText.textContent = pwa.install.isIos ? T("installIos") : T("installHint");
    installButton.hidden = true;
  }
  installButton.addEventListener("click", function () { pwa.install.prompt().then(showInstall); });
  window.addEventListener("matmail:installable", showInstall);
  showInstall();

  // ---- Notifications -----------------------------------------------------------------------------
  var text = $("push-text");
  var controls = $("push-controls");
  if (!text || !controls) { return; }
  var toggle = $("push-toggle");
  var scope = $("push-scope");
  var test = $("push-test");

  function ownOnly() { return $("push-scope-own").checked; }

  // What this device is doing stays in the line above the switch; the result of one action (a failure, the test) is a toast.
  function say(message, tone) {
    if (window.MatUi) { window.MatUi.toast(message, { tone: tone, timeout: tone === "danger" ? 9000 : undefined }); } else { text.textContent = message; }
  }

  function render(state) {
    // On an iPhone Safari offers notifications only to an app on the home screen, so the hint for that comes before "not supported".
    var blocked = state.needsApp ? T("needsApp") : !state.supported || !state.secure ? T("notSupported") : !state.enabled ? T("serverOff") : state.permission === "denied" && !state.subscribed ? T("blocked") : "";
    controls.hidden = !!blocked;
    text.textContent = blocked || (state.subscribed ? T("on") : T("off"));
    toggle.checked = state.subscribed;
    toggle.disabled = false;
    scope.hidden = false;
    $("push-scope-own").checked = state.ownMailboxOnly;
    $("push-scope-all").checked = !state.ownMailboxOnly;
    test.hidden = !state.subscribed;
  }

  function refresh() { return pwa.push.state().then(render).catch(function () { render({ supported: false, secure: true }); }); }

  toggle.addEventListener("change", function () {
    toggle.disabled = true;
    text.textContent = T("working");
    var done = toggle.checked ? pwa.push.enable(ownOnly()) : pwa.push.disable();
    done.then(function () { location.reload(); }, function (error) {
      refresh().then(function () { if (!error || !error.denied) { say(T("failed") + (error && error.message ? " " + error.message : ""), "danger"); } });
    });
  });

  // A change of the choice is the same subscription told again.
  [$("push-scope-own"), $("push-scope-all")].forEach(function (radio) {
    radio.addEventListener("change", function () {
      if (!toggle.checked) { return; }
      pwa.push.enable(ownOnly()).then(function () { location.reload(); }, refresh);
    });
  });

  test.addEventListener("click", function () {
    test.disabled = true;
    pwa.push.test().then(function () { say(T("testSent"), "success"); }, function () { say(T("testFailed"), "danger"); }).then(function () { test.disabled = false; });
  });

  refresh();
})();
