// MatMail service worker – the web client as an app.
//   • Styles, scripts and icons are kept (their addresses carry a version, so a new release brings new ones).
//   • The last mail page and the start-up data of the client are kept: it opens without a connection, and mail can be written
//     (mail-outbox.js holds it on the device until the connection is back).
//   • Push notifications (Web Push): shown here, a tap opens the message.
// Nothing else is touched: mail, attachments and every change go to the server as always. Registered by pwa.js as /sw.js?v=<version>:
// a new release is a new address, so the browser installs it and the old caches go.
"use strict";

const VERSION = new URL(self.location.href).searchParams.get("v") || "dev";
const ASSETS = "matmail-assets-" + VERSION;
const PAGES = "matmail-pages-" + VERSION;
const MAIL_PAGE = "/Mail";

self.addEventListener("install", (event) => {
  event.waitUntil(self.skipWaiting());
});

self.addEventListener("activate", (event) => {
  event.waitUntil((async () => {
    for (const name of await caches.keys()) {
      if (name.startsWith("matmail-") && name !== ASSETS && name !== PAGES) { await caches.delete(name); }
    }
    await self.clients.claim();
  })());
});

// The page tells which files it is made of, so the first visit is enough to open the client offline the next time.
self.addEventListener("message", (event) => {
  const data = event.data || {};
  if (data.type === "cache-assets" && Array.isArray(data.urls)) {
    event.waitUntil((async () => {
      const cache = await caches.open(ASSETS);
      for (const address of data.urls) {
        const url = new URL(address, self.location.origin);
        if (url.origin !== self.location.origin || !isAsset(url) || await cache.match(url.href)) { continue; }
        try {
          const response = await fetch(url.href);
          if (response.ok) { await cache.put(url.href, response); }
        } catch (error) { /* the next visit */ }
      }
    })());
  } else if (data.type === "forget") {
    // Signing out: what the pages cache holds belongs to the person who signed in.
    event.waitUntil(caches.delete(PAGES));
  }
});

function isAsset(url) {
  return /^\/(css|js|icons)\//.test(url.pathname);
}

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") { return; }
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) { return; }

  if (isAsset(url)) { event.respondWith(fromCacheFirst(request)); }
  else if (request.mode === "navigate") { event.respondWith(page(request, url)); }
  else if (url.pathname === "/api/mail/bootstrap") { event.respondWith(startupData(request)); }
});

async function fromCacheFirst(request) {
  const cache = await caches.open(ASSETS);
  const hit = await cache.match(request);
  if (hit) { return hit; }
  const response = await fetch(request);
  if (response.ok) { await cache.put(request, response.clone()); }
  return response;
}

/** A page: from the network; the mail page also from the last visit when there is no connection. */
async function page(request, url) {
  const isMail = url.pathname.toLowerCase().replace(/\/$/, "") === MAIL_PAGE.toLowerCase();
  try {
    const response = await fetch(request);
    // Kept: the page itself – not a redirect to the sign-in page and not an error.
    if (isMail && response.ok && !response.redirected && (response.headers.get("content-type") || "").includes("text/html")) {
      await (await caches.open(PAGES)).put(MAIL_PAGE, response.clone());
    }
    return response;
  } catch (error) {
    if (isMail) {
      const hit = await (await caches.open(PAGES)).match(MAIL_PAGE);
      if (hit) { return hit; }
    }
    return offlinePage();
  }
}

/** The start-up data of the client (folders, addresses, signatures): the last ones when there is no connection. */
async function startupData(request) {
  const cache = await caches.open(PAGES);
  try {
    const response = await fetch(request);
    if (response.ok) { await cache.put(request.url, response.clone()); }
    return response;
  } catch (error) {
    const hit = await cache.match(request.url);
    if (!hit) { throw error; }
    const headers = new Headers(hit.headers);
    headers.set("X-MatMail-Offline", "1");   // the client knows these are the last ones, not the current ones
    return new Response(await hit.blob(), { status: 200, headers });
  }
}

function offlinePage() {
  const german = (self.navigator.language || "").toLowerCase().startsWith("de");
  const title = german ? "Keine Verbindung" : "No connection";
  const text = german ? "Der Server ist gerade nicht erreichbar. Sobald die Verbindung wieder da ist, geht es hier weiter." : "The server cannot be reached right now. As soon as the connection is back, you can carry on here.";
  const retry = german ? "Erneut versuchen" : "Try again";
  const html = "<!doctype html><html lang=\"" + (german ? "de" : "en") + "\"><head><meta charset=\"utf-8\">"
    + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover\">"
    + "<meta name=\"color-scheme\" content=\"light dark\"><title>" + title + "</title>"
    + "<style>body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;padding:24px;box-sizing:border-box;font:16px/1.5 system-ui,-apple-system,sans-serif;background:#f4f6fb;color:#1b2130;text-align:center}"
    + "@media (prefers-color-scheme:dark){body{background:#0f131c;color:#e6e9f2}}"
    + "main{max-width:26rem}h1{font-size:1.3rem;margin:0 0 .5rem}p{margin:0 0 1.2rem;opacity:.8}"
    + "button{font:inherit;padding:.6rem 1.4rem;border:0;border-radius:999px;background:#1a73e8;color:#fff;cursor:pointer}</style></head>"
    + "<body><main><h1>" + title + "</h1><p>" + text + "</p><button onclick=\"location.reload()\">" + retry + "</button></main></body></html>";
  return new Response(html, { status: 503, headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" } });
}

// ---- Notifications -------------------------------------------------------------------------------------------------------
self.addEventListener("push", (event) => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch (error) { data = { body: event.data ? event.data.text() : "" }; }
  event.waitUntil(self.registration.showNotification(data.title || "MatMail", {
    body: data.body || "",
    icon: "/icons/icon-192.png",
    badge: "/icons/badge-96.png",
    tag: data.tag || "mail",
    renotify: true,
    data: { url: data.url || MAIL_PAGE }
  }));
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const target = new URL((event.notification.data && event.notification.data.url) || MAIL_PAGE, self.location.origin).href;
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    const open = windows.find((w) => new URL(w.url).pathname.toLowerCase().startsWith(MAIL_PAGE.toLowerCase()));
    if (open) {
      await open.focus();
      open.postMessage({ type: "open", url: target });   // the page changes its view, it is not loaded again
    } else {
      await self.clients.openWindow(target);
    }
  })());
});

// The browser dropped the subscription (or the key changed): a new one is made and the server is told.
self.addEventListener("pushsubscriptionchange", (event) => {
  event.waitUntil((async () => {
    try {
      const old = event.oldSubscription;
      const key = old && old.options ? old.options.applicationServerKey : null;
      if (!key) { return; }
      const subscription = await self.registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
      const token = (await (await fetch("/api/mail/csrf", { credentials: "same-origin" })).json()).token;
      await fetch("/api/mail/push/subscribe", {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
        body: JSON.stringify(Object.assign(subscription.toJSON(), { replaces: old.endpoint }))
      });
    } catch (error) { /* the page asks again the next time it is opened */ }
  })());
});
