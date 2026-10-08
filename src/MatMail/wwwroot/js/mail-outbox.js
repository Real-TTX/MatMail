// MatMail web client – the outbox on the device: mail that is written without a connection is kept here (IndexedDB) and sent as
// soon as the connection is back. A message either waits to be sent ("send") or only to be saved as a draft ("draft", when the window
// was closed offline). Attachments added offline are kept as files and uploaded when the message goes out.
// The items belong to the user who wrote them: another user of the same browser neither sees nor sends them.
(function () {
  "use strict";

  var App = window.MailApp;
  var DB_NAME = "matmail";
  var STORE = "outbox";
  var database = null;
  var flushing = null;

  /** Items waiting for the signed-in user (kept up to date for the folder list). */
  var state = { count: 0, failed: 0 };

  App.outbox = { add: add, list: list, take: take, remove: remove, flush: flush, state: state, refresh: refresh, isOffline: isOffline, isNetworkError: isNetworkError, open: openDialog };

  function isOffline() { return navigator.onLine === false; }

  /** A failed fetch (no answer at all) is a lost connection; an answer with a status is the server saying no. */
  function isNetworkError(error) { return !!error && error.status === undefined; }

  function userId() { return App.boot && App.boot.user ? App.boot.user.id : 0; }

  function openDatabase() {
    if (database) { return Promise.resolve(database); }
    return new Promise(function (resolve, reject) {
      if (!window.indexedDB) { reject(new Error("no storage")); return; }
      var request = indexedDB.open(DB_NAME, 1);
      request.onupgradeneeded = function () { request.result.createObjectStore(STORE, { keyPath: "id", autoIncrement: true }); };
      request.onsuccess = function () { database = request.result; resolve(database); };
      request.onerror = function () { reject(request.error); };
    });
  }

  function run(mode, work) {
    return openDatabase().then(function (db) {
      return new Promise(function (resolve, reject) {
        var transaction = db.transaction(STORE, mode);
        var result;
        transaction.oncomplete = function () { resolve(result); };
        transaction.onerror = function () { reject(transaction.error); };
        transaction.onabort = function () { reject(transaction.error); };
        work(transaction.objectStore(STORE), function (value) { result = value; });
      });
    });
  }

  function changed() {
    return refresh().then(function () { window.dispatchEvent(new CustomEvent("matmail:outbox")); });
  }

  /**
   * Keeps a message: kind "send" or "draft", the model of the compose window, the files that are not on the server yet and the
   * attachments that are (their names, for when the message is opened again).
   */
  function add(kind, model, files, uploaded) {
    var item = { userId: userId(), kind: kind, created: new Date().toISOString(), model: model, files: files || [], uploaded: uploaded || [], error: null };
    return run("readwrite", function (store, done) {
      var request = store.add(item);
      request.onsuccess = function () { done(request.result); };
    }).then(function (id) { return changed().then(function () { return id; }); });
  }

  /** What waits for the signed-in user, oldest first. */
  function list() {
    var wanted = userId();
    return run("readonly", function (store, done) {
      var request = store.getAll();
      request.onsuccess = function () { done(request.result.filter(function (item) { return item.userId === wanted; })); };
    }).then(function (items) { return (items || []).sort(function (a, b) { return a.id - b.id; }); });
  }

  function remove(id) {
    return run("readwrite", function (store) { store.delete(id); }).then(changed);
  }

  /** Takes an item out of the outbox (to write it again): it is gone from here and returned. */
  function take(id) {
    return run("readwrite", function (store, done) {
      var request = store.get(id);
      request.onsuccess = function () { done(request.result); store.delete(id); };
    }).then(function (item) { return changed().then(function () { return item; }); });
  }

  function update(item) {
    return run("readwrite", function (store) { store.put(item); });
  }

  function refresh() {
    return list().then(function (items) {
      state.count = items.length;
      state.failed = items.filter(function (i) { return i.error; }).length;
    }, function () { state.count = 0; state.failed = 0; });
  }

  // ---------------------------------------------------------------------------------------------
  // Sending what is kept
  // ---------------------------------------------------------------------------------------------
  function token() {
    return App.get("/api/mail/csrf").then(function (data) {
      var meta = document.querySelector('meta[name="csrf-token"]');
      if (meta) { meta.setAttribute("content", data.token); }   // the page's own token may be old; the calls below use this one
      return data.token;
    });
  }

  function upload(file) {
    var form = new FormData();
    form.append("file", new File([file.blob], file.name, { type: file.type || "application/octet-stream" }));
    return App.api("POST", "/api/mail/attachments", form);
  }

  function sendOne(item) {
    var ids = (item.model.attachmentIds || []).slice();
    var chain = Promise.resolve();
    (item.files || []).forEach(function (file) {
      chain = chain.then(function () { return file.uploadedId ? file.uploadedId : upload(file).then(function (data) { file.uploadedId = data.id; return update(item).then(function () { return data.id; }); }); }).then(function (id) { ids.push(id); });
    });
    return chain.then(function () {
      var model = Object.assign({}, item.model, { attachmentIds: ids });
      return item.kind === "send" ? App.post("/api/mail/send", model) : App.post("/api/mail/drafts", model);
    });
  }

  /** Sends everything that waits, oldest first. Stops at the first lost connection; what the server refuses stays, with its reason. */
  function flush() {
    if (flushing) { return flushing; }
    if (isOffline() || !App.boot) { return Promise.resolve({ sent: 0, saved: 0, remaining: state.count }); }
    var summary = { sent: 0, saved: 0, remaining: 0, signIn: false };
    flushing = list().then(function (items) {
      if (!items.length) { return summary; }
      return token().then(function () {
        return items.reduce(function (previous, item) {
          return previous.then(function (stop) {
            if (stop) { return true; }
            return sendOne(item).then(function () {
              if (item.kind === "send") { summary.sent++; } else { summary.saved++; }
              return remove(item.id).then(function () { return false; });
            }, function (error) {
              if (isNetworkError(error)) { return true; }                    // offline again: the rest stays
              if (error.status === 401 || error.message === "unauthorized") { summary.signIn = true; return true; }   // the session ended: it goes on after signing in
              item.error = error.message || String(error.status);            // the server said no: the person decides
              return update(item).then(function () { return false; });
            });
          });
        }, Promise.resolve(false)).then(function () { return summary; });
      });
    }).catch(function () { return summary; }).then(function (result) {
      return refresh().then(function () {
        result.remaining = state.count;
        window.dispatchEvent(new CustomEvent("matmail:outbox", { detail: result }));
        flushing = null;
        return result;
      });
    });
    return flushing;
  }

  // ---------------------------------------------------------------------------------------------
  // What is waiting: a window to look at it
  // ---------------------------------------------------------------------------------------------
  function openDialog() {
    return list().then(function (items) {
      var dialog = App.el("dialog", { class: "dialog dialog--sm outbox-dialog" });
      var body = App.el("div", { class: "dialog__body outbox" });
      var send = App.el("button", { type: "button", class: "btn btn--primary", text: App.T("sendNow") });
      var close = App.el("button", { type: "button", class: "btn btn--secondary", text: App.T("close") });
      dialog.appendChild(App.el("div", { class: "dialog__header" }, [App.el("h2", { class: "dialog__title", text: App.T("outbox") }), App.iconButton("x", App.T("close"), function () { dialog.close(); })]));

      function render(current) {
        body.innerHTML = "";
        if (!current.length) { body.appendChild(App.el("p", { class: "muted", text: App.T("outboxEmpty") })); }
        current.forEach(function (item) {
          var recipients = (item.model.to || []).concat(item.model.cc || []).join(", ");
          var row = App.el("div", { class: "outbox__item" }, [
            App.el("div", { class: "outbox__main" }, [
              App.el("b", { text: item.model.subject || App.T("noSubject") }),
              App.el("span", { class: "muted", text: (item.kind === "draft" ? App.T("outboxDraft") : recipients || App.T("noRecipient")) + " · " + App.formatListDate(item.created) }),
              item.error ? App.el("span", { class: "outbox__error", text: item.error }) : null
            ])
          ]);
          var edit = App.iconButton("edit", App.T("outboxEdit"), function () { dialog.close(); take(item.id).then(function (taken) { App.compose.openFromOutbox(taken); }); });
          var discard = App.iconButton("trash", App.T("delete"), function () { remove(item.id).then(list).then(render); }, "icon-btn--danger");
          row.appendChild(App.el("div", { class: "outbox__actions" }, [edit, discard]));
          body.appendChild(row);
        });
        send.disabled = !current.length || isOffline();
      }

      send.addEventListener("click", function () {
        send.disabled = true;
        flush().then(function (result) {
          return list().then(function (rest) {
            render(rest);
            if (result.sent + result.saved) { App.toast(App.T("outboxSent").replace("{0}", result.sent + result.saved)); }
            if (!rest.length) { dialog.close(); if (App.handlers.reloadList) { App.handlers.reloadList(); } }
          });
        });
      });
      close.addEventListener("click", function () { dialog.close(); });
      dialog.appendChild(body);
      dialog.appendChild(App.el("div", { class: "dialog__footer" }, [send, close]));
      dialog.addEventListener("close", function () { dialog.remove(); });
      document.body.appendChild(dialog);
      render(items);
      dialog.showModal();
    });
  }

  // ---------------------------------------------------------------------------------------------
  // The connection comes and goes
  // ---------------------------------------------------------------------------------------------
  window.addEventListener("online", function () { flush(); });
  setInterval(function () { if (state.count && !isOffline()) { flush(); } }, 30000);   // a lost connection that the browser did not announce
})();
