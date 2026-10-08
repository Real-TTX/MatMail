// MatMail – the app around the pages: the service worker (/sw.js), installing, notifications, and what makes a page behave like
// an app on a phone (no pinch zoom, no page left shifted after the keyboard closes). Loaded by both layouts.
// window.MatMailPwa is what the account page uses for the install button and the notification switch.
(function () {
  "use strict";

  var doc = document;
  var script = doc.currentScript;
  var workerUrl = script && script.getAttribute("data-worker");
  var ua = navigator.userAgent || "";
  var isIos = /iPad|iPhone|iPod/.test(ua) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);

  // ---------------------------------------------------------------------------------------------
  // Service worker
  // ---------------------------------------------------------------------------------------------
  var ready = Promise.resolve(null);
  if ("serviceWorker" in navigator && workerUrl) {
    ready = new Promise(function (resolve) {
      var start = function () {
        navigator.serviceWorker.register(workerUrl, { scope: "/" }).then(function () { return navigator.serviceWorker.ready; }).then(function (registration) {
          // The page tells the worker what it is made of, so that the next visit works without a connection.
          var urls = [].slice.call(doc.querySelectorAll('link[rel="stylesheet"][href], script[src]')).map(function (node) { return node.href || node.src; });
          if (registration.active) { registration.active.postMessage({ type: "cache-assets", urls: urls }); }
          resolve(registration);
        }).catch(function () { resolve(null); });
      };
      if (doc.readyState === "complete") { start(); } else { window.addEventListener("load", start); }
    });

    // A tap on a notification: the page that is open changes its view instead of being loaded again.
    navigator.serviceWorker.addEventListener("message", function (event) {
      var data = event.data || {};
      if (data.type !== "open" || !data.url) { return; }
      var target = new URL(data.url, location.href);
      if (target.pathname.toLowerCase().replace(/\/$/, "") === location.pathname.toLowerCase().replace(/\/$/, "")) { location.hash = target.hash; }
      else { location.href = target.href; }
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Install
  // ---------------------------------------------------------------------------------------------
  var installEvent = null;
  window.addEventListener("beforeinstallprompt", function (event) {
    event.preventDefault();
    installEvent = event;
    window.dispatchEvent(new CustomEvent("matmail:installable"));
  });
  window.addEventListener("appinstalled", function () { installEvent = null; window.dispatchEvent(new CustomEvent("matmail:installable")); });

  function isStandalone() {
    return (window.matchMedia && window.matchMedia("(display-mode: standalone)").matches) || navigator.standalone === true;
  }

  // ---------------------------------------------------------------------------------------------
  // Notifications (Web Push)
  // ---------------------------------------------------------------------------------------------
  var csrfToken = null;
  function api(method, url, body) {
    var token = csrfToken ? Promise.resolve(csrfToken) : (method === "GET" ? Promise.resolve("") : fetch("/api/mail/csrf", { credentials: "same-origin" }).then(function (r) { return r.json(); }).then(function (d) { csrfToken = d.token; return csrfToken; }));
    return token.then(function (t) {
      var headers = { "Accept": "application/json" };
      var init = { method: method, credentials: "same-origin", headers: headers };
      if (method !== "GET") { headers["X-CSRF-TOKEN"] = t; }
      if (body !== undefined) { headers["Content-Type"] = "application/json"; init.body = JSON.stringify(body); }
      return fetch(url, init);
    }).then(function (response) {
      var type = response.headers.get("content-type") || "";
      return (type.indexOf("json") >= 0 ? response.json() : Promise.resolve(null)).then(function (data) {
        if (!response.ok) { var error = new Error((data && (data.error || data.title)) || ("HTTP " + response.status)); error.status = response.status; throw error; }
        return data;
      });
    });
  }

  function keyBytes(text) {
    var padded = (text + "====".slice(text.length % 4 || 4)).replace(/-/g, "+").replace(/_/g, "/");
    var raw = atob(padded);
    var bytes = new Uint8Array(raw.length);
    for (var i = 0; i < raw.length; i++) { bytes[i] = raw.charCodeAt(i); }
    return bytes;
  }

  function sameKey(buffer, text) {
    if (!buffer) { return false; }
    var a = new Uint8Array(buffer), b = keyBytes(text);
    if (a.length !== b.length) { return false; }
    for (var i = 0; i < a.length; i++) { if (a[i] !== b[i]) { return false; } }
    return true;
  }

  var push = {
    /** The browser can do it at all (needs https or localhost; on an iPhone also the app on the home screen). */
    supported: "serviceWorker" in navigator && "PushManager" in window && "Notification" in window,

    /** What the page needs to show the right switch. */
    state: function () {
      var result = { supported: push.supported, permission: "Notification" in window ? Notification.permission : "denied", subscribed: false, enabled: true, ownMailboxOnly: true, needsApp: isIos && !isStandalone(), secure: window.isSecureContext !== false };
      if (!push.supported) { return Promise.resolve(result); }
      return Promise.all([ready, api("GET", "/api/mail/push/config")]).then(function (values) {
        var registration = values[0], config = values[1];
        result.enabled = !!config.enabled;
        if (!registration || !config.enabled) { return result; }
        return registration.pushManager.getSubscription().then(function (subscription) {
          if (!subscription) { return result; }
          // A subscription made for another key of the server (a new installation) is of no use: it is dropped.
          if (!sameKey(subscription.options && subscription.options.applicationServerKey, config.publicKey)) { return subscription.unsubscribe().then(function () { return result; }); }
          return api("POST", "/api/mail/push/status", { endpoint: subscription.endpoint }).then(function (status) {
            result.subscribed = !!status.subscribed;
            result.ownMailboxOnly = status.ownMailboxOnly !== false;
            return result;
          });
        });
      });
    },

    /** Asks for permission (this must come from a tap), subscribes and tells the server. */
    enable: function (ownMailboxOnly) {
      return Notification.requestPermission().then(function (permission) {
        if (permission !== "granted") { var denied = new Error("denied"); denied.denied = true; throw denied; }
        return Promise.all([ready, api("GET", "/api/mail/push/config")]);
      }).then(function (values) {
        var registration = values[0], config = values[1];
        if (!registration) { throw new Error("no service worker"); }
        return registration.pushManager.getSubscription().then(function (existing) {
          if (existing && sameKey(existing.options && existing.options.applicationServerKey, config.publicKey)) { return existing; }
          return (existing ? existing.unsubscribe() : Promise.resolve()).then(function () {
            return registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(config.publicKey) });
          });
        });
      }).then(function (subscription) {
        return api("POST", "/api/mail/push/subscribe", Object.assign(subscription.toJSON(), { ownMailboxOnly: ownMailboxOnly !== false }));
      });
    },

    /** Stops the notifications on this device. */
    disable: function () {
      return ready.then(function (registration) {
        return registration ? registration.pushManager.getSubscription() : null;
      }).then(function (subscription) {
        if (!subscription) { return null; }
        var endpoint = subscription.endpoint;
        return api("POST", "/api/mail/push/unsubscribe", { endpoint: endpoint }).catch(function () { /* the server forgets it when it cannot reach it */ }).then(function () { return subscription.unsubscribe(); });
      });
    },

    /** A notification to this device only, to see that it works. */
    test: function () {
      return ready.then(function (registration) { return registration.pushManager.getSubscription(); }).then(function (subscription) {
        if (!subscription) { throw new Error("not subscribed"); }
        return api("POST", "/api/mail/push/test", { endpoint: subscription.endpoint });
      });
    }
  };

  window.MatMailPwa = {
    push: push,
    install: {
      available: function () { return !!installEvent; },
      isStandalone: isStandalone,
      isIos: isIos,
      prompt: function () {
        if (!installEvent) { return Promise.resolve("unavailable"); }
        var event = installEvent;
        installEvent = null;
        event.prompt();
        return event.userChoice.then(function (choice) { window.dispatchEvent(new CustomEvent("matmail:installable")); return choice.outcome; });
      }
    }
  };

  // Signing out: what the app kept for this person goes, and the device stops getting their notifications.
  doc.addEventListener("submit", function (event) {
    var form = event.target;
    if (!form || form.tagName !== "FORM" || !form.action || !/\/account\/logout$/i.test(new URL(form.action, location.href).pathname)) { return; }
    if (navigator.serviceWorker && navigator.serviceWorker.controller) { navigator.serviceWorker.controller.postMessage({ type: "forget" }); }
    if (!push.supported || Notification.permission !== "granted") { return; }
    event.preventDefault();
    var go = function () { form.submit(); };
    push.disable().then(go, go);
    setTimeout(go, 4000);   // a slow answer must not keep anybody signed in
  });

  // ---------------------------------------------------------------------------------------------
  // Behaving like an app
  // ---------------------------------------------------------------------------------------------
  // iPhones ignore user-scalable=no; a pinch is a gesture event there.
  ["gesturestart", "gesturechange", "gestureend"].forEach(function (type) {
    doc.addEventListener(type, function (event) { event.preventDefault(); }, { passive: false });
  });

  // iOS pans the whole app up to show the field that is typed in and does not pan it back when the keyboard closes.
  if (isIos) {
    var typing = function () {
      var active = doc.activeElement;
      return !!active && (/^(INPUT|TEXTAREA|SELECT)$/.test(active.tagName) || active.isContentEditable);
    };
    var settle = function () {
      if (typing()) { return; }
      var viewport = window.visualViewport;
      if (viewport && viewport.scale > 1.01) { return; }
      window.scrollTo(0, 0);
    };
    doc.addEventListener("focusout", function () { setTimeout(settle, 150); });
    if (window.visualViewport) { window.visualViewport.addEventListener("resize", function () { setTimeout(settle, 150); }); }
  }
})();
