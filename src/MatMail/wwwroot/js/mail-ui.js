// MatMail web client – the mail UI: folder tree, message list, reader, search, routing, shortcuts and live updates.
(function () {
  "use strict";

  var App = window.MailApp;
  var doc = document;
  var S = App.state;
  var T = App.T;

  var els = {};
  var KIND_ICONS = { Inbox: "inbox", Drafts: "file-text", Sent: "send", Archive: "archive", Junk: "spam", Trash: "trash", Custom: "folder" };

  // ---------------------------------------------------------------------------------------------
  // Start
  // ---------------------------------------------------------------------------------------------
  function init() {
    els.app = doc.getElementById("mail-app");
    els.panes = doc.getElementById("mail-panes");
    els.drop = doc.getElementById("mail-drop");
    els.file = doc.getElementById("mail-file");
    els.splitter = doc.getElementById("mail-splitter");
    els.folders = doc.getElementById("mail-folders");
    els.listPane = doc.getElementById("mail-list-pane");
    els.readerPane = doc.getElementById("mail-reader-pane");
    els.listToolbar = doc.getElementById("mail-list-toolbar");
    els.readerToolbar = doc.getElementById("mail-reader-toolbar");
    els.list = doc.getElementById("mail-list");
    els.reader = doc.getElementById("mail-reader");
    els.empty = doc.getElementById("mail-empty");
    els.emptyText = doc.getElementById("mail-empty-text");
    els.notice = doc.getElementById("mail-notice");
    els.loading = doc.getElementById("mail-loading");
    els.selectBar = doc.getElementById("mail-selectbar");
    els.offline = doc.getElementById("mail-offline");
    els.search = doc.getElementById("mail-search");
    els.searchInput = doc.getElementById("mail-search-input");
    els.searchClear = doc.getElementById("mail-search-clear");

    App.handlers.renderFolders = renderFolders;
    App.handlers.reloadList = function () { return loadList(true); };

    // "Synchronise now" of the account menu (app.js): the fetched mail is shown, the counts and the storage follow.
    window.addEventListener("matmail:synced", function () {
      if (!App.boot) { return; }
      refreshBootstrap().then(function () { if (!S.messageId) { loadList(true); } });
    });

    doc.getElementById("mail-compose-button").addEventListener("click", function () { App.compose.open({}); });
    doc.getElementById("mail-compose-fab").addEventListener("click", function () { App.compose.open({}); });
    els.search.addEventListener("submit", function (e) { e.preventDefault(); search(els.searchInput.value.trim()); });
    els.searchClear.addEventListener("click", function () { els.searchInput.value = ""; navigate({ q: "", page: 1, m: 0 }); els.searchInput.focus(); });
    els.searchInput.addEventListener("input", function () { els.searchClear.hidden = !els.searchInput.value; });
    App.search.attach({ wrap: els.search, input: els.searchInput, options: doc.getElementById("mail-search-options"), run: search });
    window.addEventListener("hashchange", route);
    doc.addEventListener("keydown", onKey);
    setupSplitter();
    setupFileDrops();
    applyLayout();
    // A window that grows or shrinks past the width of the reading pane changes the layout; the screen is drawn again for it.
    var onWidth = function () { applyLayout(); if (App.boot) { route(); renderList(); } };
    if (wide.addEventListener) { wide.addEventListener("change", onWidth); } else { wide.addListener(onWidth); }

    // The connection comes and goes: the banner follows, and what was written offline goes out (mail-outbox.js).
    els.offline.textContent = T("offlineNotice");
    window.addEventListener("offline", updateOffline);
    window.addEventListener("online", function () { updateOffline(); if (App.boot) { refreshBootstrap(); } });
    window.addEventListener("matmail:outbox", function (e) {
      renderFolders();
      var result = e.detail;
      if (result && result.sent + result.saved > 0) {
        App.toast(T("outboxSent").replace("{0}", result.sent + result.saved));
        refreshCounts();
        if (!S.messageId) { loadList(true); }
      }
    });
    updateOffline();

    showLoading(true);
    App.get("/api/mail/bootstrap").then(function (boot) {
      App.boot = boot;
      App.compose.setup();
      renderFolders();
      route();
      connectEvents();
      App.outbox.refresh().then(function () { renderFolders(); App.outbox.flush(); });
      openComposeShortcut();
    }).catch(function (error) {
      showLoading(false);
      showNotice(T("loadFailed") + " " + error.message, true);
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Routing: the address after # carries the whole view (mailbox, folder, page, search text, open message)
  // ---------------------------------------------------------------------------------------------
  function readHash() {
    var params = new URLSearchParams(location.hash.replace(/^#/, ""));
    return {
      mailbox: parseInt(params.get("mailbox"), 10) || 0,
      folder: parseInt(params.get("folder"), 10) || 0,
      page: parseInt(params.get("page"), 10) || 1,
      q: params.get("q") || "",
      m: parseInt(params.get("m"), 10) || 0,
      p: params.get("p") || ""   // a message file that is open (nothing of it is in the mailbox)
    };
  }

  function navigate(changes) {
    var current = { mailbox: S.mailboxId, folder: S.folderId, page: S.page, q: S.query, m: S.messageId, p: "" };   // an open file is only kept when it is asked for
    Object.keys(changes).forEach(function (key) { current[key] = changes[key]; });
    var params = new URLSearchParams();
    if (current.mailbox) { params.set("mailbox", current.mailbox); }
    if (current.folder) { params.set("folder", current.folder); }
    if (current.page > 1) { params.set("page", current.page); }
    if (current.q) { params.set("q", current.q); }
    if (current.m) { params.set("m", current.m); }
    if (current.p) { params.set("p", current.p); }
    var hash = "#" + params.toString();
    if (location.hash === hash) { route(); } else { location.hash = hash; }
  }

  function updateOffline() { els.offline.hidden = !App.outbox.isOffline(); }

  /** The shortcut of the installed app ("New message", long press on the icon) opens the address /Mail?compose=1. */
  function openComposeShortcut() {
    if (new URLSearchParams(location.search).get("compose") !== "1") { return; }
    history.replaceState(null, "", location.pathname + location.hash);
    App.compose.open({});
  }

  /** A search covers the whole mailbox (without spam and trash), like in Gmail; in: narrows it down. Without text the list is back where it was. */
  function search(text) {
    navigate(text ? { q: text, folder: 0, page: 1, m: 0 } : { q: "", page: 1, m: 0 });
  }

  function route() {
    if (!App.boot) { return; }
    var h = readHash();
    var mailbox = App.mailbox(h.mailbox) || App.boot.mailboxes[0];
    if (!mailbox) { showLoading(false); showNotice(T("noMailbox"), false); return; }
    var folder = mailbox.folders.filter(function (f) { return f.id === h.folder; })[0];
    if (!folder && !h.q) { folder = mailbox.folders.filter(function (f) { return f.kind === "Inbox"; })[0] || mailbox.folders[0]; }

    var sameList = S.mailboxId === mailbox.id && S.folderId === (folder ? folder.id : 0) && S.page === h.page && S.query === h.q;
    if (!sameList) { S.allMatching = false; }
    S.mailboxId = mailbox.id;
    S.folderId = folder ? folder.id : 0;
    S.page = h.page;
    S.query = h.q;
    var previousMessage = S.messageId;
    var previousPreview = S.previewId;
    S.messageId = h.m;
    S.previewId = h.p;
    els.searchInput.value = h.q;
    els.searchClear.hidden = !h.q;
    renderFolders();
    if (window.MatMail && window.MatMail.setSidebarOpen) { window.MatMail.setSidebarOpen(false); }

    if (h.p || h.m) {
      showReader();
      if (h.p ? previousPreview !== h.p : previousMessage !== h.m) { if (h.p) { loadPreview(h.p); } else { loadMessage(h.m); } }
      // With a reading pane the list stays on the screen next to the message, so it has to be the right one.
      if (isSplit() ? (!sameList || !S.items.length) : (!sameList && !S.items.length)) { loadList(true); }
    } else {
      showList();
      if (!sameList || ((previousMessage || previousPreview) && !isSplit())) { loadList(false); }
    }
    markOpenRow();
  }

  function showList() {
    els.listPane.hidden = false;
    els.readerPane.hidden = !isSplit();
    els.app.classList.remove("is-reading");
    if (isSplit()) { showPlaceholder(); }
    doc.title = pageTitle();
  }
  function showReader() { els.listPane.hidden = !isSplit(); els.readerPane.hidden = false; els.app.classList.add("is-reading"); }

  // ---------------------------------------------------------------------------------------------
  // Layout: the reading pane of the appearance settings, beside or below the list (wide screens only)
  // ---------------------------------------------------------------------------------------------
  var wide = window.matchMedia("(min-width: 961px)");

  /** "right", "below" or "": the reading pane the user chose, when the screen has room for it. */
  function splitMode() {
    var mode = doc.documentElement.getAttribute("data-reading-pane");
    return wide.matches && (mode === "right" || mode === "below") ? mode : "";
  }
  function isSplit() { return splitMode() !== ""; }

  function applyLayout() {
    var mode = splitMode();
    els.app.classList.toggle("is-split", !!mode);
    els.app.classList.toggle("is-split-right", mode === "right");
    els.app.classList.toggle("is-split-below", mode === "below");
    els.splitter.hidden = !mode;
    els.splitter.setAttribute("aria-orientation", mode === "below" ? "horizontal" : "vertical");
    var saved = mode ? parseInt(localStorage.getItem("matmail-split-" + mode), 10) : 0;
    if (saved > 0) { els.panes.style.setProperty("--mail-list-size", saved + "px"); } else { els.panes.style.removeProperty("--mail-list-size"); }
  }

  /** The bar between list and reader: dragged with the pointer or moved with the arrow keys; the size is remembered per layout. */
  function setupSplitter() {
    var dragging = false;
    function sizeAt(e) {
      var rect = els.panes.getBoundingClientRect();
      var below = splitMode() === "below";
      var total = below ? rect.height : rect.width;
      var value = below ? e.clientY - rect.top : e.clientX - rect.left;
      return Math.round(Math.max(total * 0.2, Math.min(total * 0.8, value)));
    }
    function remember(size) {
      els.panes.style.setProperty("--mail-list-size", size + "px");
      localStorage.setItem("matmail-split-" + splitMode(), String(size));
    }
    els.splitter.addEventListener("pointerdown", function (e) {
      dragging = true;
      els.splitter.setPointerCapture(e.pointerId);
      els.splitter.classList.add("is-dragging");
      e.preventDefault();
    });
    els.splitter.addEventListener("pointermove", function (e) { if (dragging) { els.panes.style.setProperty("--mail-list-size", sizeAt(e) + "px"); } });
    els.splitter.addEventListener("pointerup", function (e) {
      if (!dragging) { return; }
      dragging = false;
      els.splitter.classList.remove("is-dragging");
      remember(sizeAt(e));
    });
    els.splitter.addEventListener("pointercancel", function () { dragging = false; els.splitter.classList.remove("is-dragging"); });
    els.splitter.addEventListener("keydown", function (e) {
      var below = splitMode() === "below";
      var step = e.key === (below ? "ArrowDown" : "ArrowRight") ? 24 : e.key === (below ? "ArrowUp" : "ArrowLeft") ? -24 : 0;
      if (!step) { return; }
      e.preventDefault();
      var box = els.panes.getBoundingClientRect();
      var rect = els.listPane.getBoundingClientRect();
      var total = below ? box.height : box.width;
      remember(Math.round(Math.max(total * 0.2, Math.min(total * 0.8, (below ? rect.height : rect.width) + step))));
    });
  }

  /** What the reader pane says while no message is open. */
  function showPlaceholder() {
    messageRequest++;
    els.readerToolbar.innerHTML = "";
    els.reader.innerHTML = "";
    els.reader.appendChild(App.el("div", { class: "mail-reader-empty" }, [App.icon("mail-open"), App.el("p", { text: T("selectMessage") })]));
  }

  /** The row of the message that is open in the reading pane. */
  function markOpenRow() {
    var open = isSplit() ? S.messageId : 0;
    els.list.querySelectorAll(".mail-row").forEach(function (row) {
      row.classList.toggle("is-open", open !== 0 && parseInt(row.getAttribute("data-id"), 10) === open);
    });
  }
  function showLoading(on) { els.loading.hidden = !on; }
  function pageTitle() {
    var total = 0;
    App.boot.mailboxes.forEach(function (m) { m.folders.forEach(function (f) { if (f.kind === "Inbox" && m.isOwn) { total += f.unread; } }); });
    return (total ? "(" + total + ") " : "") + T("mail") + " · MatMail";
  }

  // ---------------------------------------------------------------------------------------------
  // Sidebar: folders per mailbox
  // ---------------------------------------------------------------------------------------------
  function renderFolders() {
    if (!App.boot) { return; }
    els.folders.innerHTML = "";
    if (App.outbox.state.count) {
      // What was written without a connection and waits for it: always in sight until it is gone.
      var waiting = App.el("button", { type: "button", class: "folder-item folder-item--outbox", title: T("outbox") }, [
        App.icon("clock"), App.el("span", { class: "folder-item__name", text: T("outbox") }), App.el("span", { class: "folder-item__count", text: String(App.outbox.state.count) })
      ]);
      waiting.addEventListener("click", function () { App.outbox.open(); });
      els.folders.appendChild(waiting);
    }
    App.boot.mailboxes.forEach(function (box, index) {
      var group = App.el("div", { class: "folder-group" });
      var collapsedKey = "matmail-collapsed-" + box.id;
      var hasSelection = box.id === S.mailboxId;
      var collapsed = !box.isOwn && !hasSelection && localStorage.getItem(collapsedKey) !== "0";

      // The mailbox is the top of its tree, the parent of its folders: its menu (the three dots, or the right button) has the info,
      // and a folder that is dropped on it goes to the top level.
      var head = App.el("div", { class: "folder-group__head" });
      var heading = App.el("button", { type: "button", class: "folder-group__title", "aria-expanded": String(!collapsed) }, [
        box.isOwn ? null : App.icon(collapsed ? "chevron-right" : "chevron-down"),   // the own mailbox is always open
        App.icon(box.type === "Shared" ? "users" : box.type === "Unassigned" ? "alert-circle" : "user"),
        App.el("span", { class: "folder-group__name", text: box.isOwn ? T("myMailbox") : box.name })
      ]);
      heading.addEventListener("click", function () { if (box.isOwn) { return; } localStorage.setItem(collapsedKey, collapsed ? "0" : "1"); renderFolders(); });
      head.appendChild(heading);
      var boxMore = App.iconButton("more-vertical", T("more"), function (e) { e.preventDefault(); e.stopPropagation(); mailboxMenu(boxMore, box); }, "folder-group__more");
      head.appendChild(boxMore);
      head.addEventListener("contextmenu", function (e) { e.preventDefault(); mailboxMenu(head, box); });
      makeRootTarget(head, box);
      group.appendChild(head);

      if (!collapsed) {
        // The folders come in tree order (every folder is followed by its subfolders): a folded folder hides what follows it
        // that is deeper. The folder that is open is never hidden: its parents unfold.
        var hideBelow = null;
        box.folders.forEach(function (folder, index) {
          if (hideBelow !== null && folder.depth > hideBelow) { return; }
          hideBelow = null;
          var below = subtreeOf(box, index);
          var folded = below.length > 0 && isFolded(folder) && !below.some(function (f) { return box.id === S.mailboxId && f.id === S.folderId; });
          group.appendChild(folderItem(box, folder, { hasChildren: below.length > 0, folded: folded, hiddenUnread: folded ? below.reduce(function (sum, f) { return sum + f.unread; }, 0) : 0 }));
          if (folded) { hideBelow = folder.depth; }
        });
        if (box.canManage && box.type !== "Unassigned") {
          var add = App.el("button", { type: "button", class: "folder-item folder-item--add" }, [App.icon("plus"), App.el("span", { text: T("newFolder") })]);
          add.addEventListener("click", function () { createFolder(box, null); });
          group.appendChild(add);
        }

        // What the mailbox takes up on the server (what stays at the provider with live access is not counted).
        var usage = usageText(box);
        if (usage) {
          group.appendChild(App.el("div", { class: "folder-usage", title: usage.title }, [App.icon("hard-drive"), App.el("span", { text: usage.text })]));
          if (usage.percent !== null) {
            group.appendChild(App.el("div", { class: "folder-usage-bar" + (usage.state ? " is-" + usage.state : ""), role: "progressbar", "aria-valuemin": "0", "aria-valuemax": "100", "aria-valuenow": String(usage.percent) }, [App.el("span", { style: "width:" + Math.max(usage.percent, box.usedBytes ? 1 : 0) + "%" })]));
          }
        }
      }

      els.folders.appendChild(group);
    });
    doc.title = pageTitle();
  }

  /** The folders below the one at <index> (they follow it in the list and are deeper). */
  function subtreeOf(box, index) {
    var depth = box.folders[index].depth, below = [];
    for (var i = index + 1; i < box.folders.length && box.folders[i].depth > depth; i++) { below.push(box.folders[i]); }
    return below;
  }

  function isFolded(folder) { return localStorage.getItem("matmail-fold-" + folder.id) === "1"; }
  function setFolded(folder, folded) {
    if (folded) { localStorage.setItem("matmail-fold-" + folder.id, "1"); } else { localStorage.removeItem("matmail-fold-" + folder.id); }
    renderFolders();
  }

  function usageText(box) {
    if (!box.messageCount && !box.usedBytes && !box.quotaBytes) { return null; }
    var title = T("storageMessages").replace("{0}", Number(box.messageCount).toLocaleString());
    if (box.remoteBytes) { title += " · " + T("storageRemote").replace("{0}", App.formatSize(box.remoteBytes)); }
    // With a limit the line says how much of it is used, and a bar shows it.
    var text = box.quotaBytes ? T("storageOf").replace("{0}", App.formatSize(box.usedBytes)).replace("{1}", App.formatSize(box.quotaBytes)) : T("storageUsed").replace("{0}", App.formatSize(box.usedBytes));
    var level = box.quotaBytes ? usageLevel(box.usedBytes, box.quotaBytes) : null;
    return { text: text, title: title, percent: level ? level.percent : null, state: level ? level.state : "" };
  }

  /** How much of a limit is used, in whole percent (100 only when it is reached), and the state that colours the bar: warn from 75 %, high from 90 %, full. */
  function usageLevel(used, quota) {
    var percent = Math.min(100, Math.floor(used * 100 / quota));
    return { percent: percent, state: used >= quota ? "full" : percent >= 90 ? "high" : percent >= 75 ? "warn" : "" };
  }

  function folderItem(box, folder, tree) {
    tree = tree || {};
    var active = box.id === S.mailboxId && folder.id === S.folderId && !S.query;
    var own = folder.kind === "Drafts" ? folder.total : folder.unread;
    var count = own + (tree.hiddenUnread || 0);
    var labelText = App.folderLabel(folder);
    var node = App.el("a", {
      class: "folder-item" + (active ? " is-active" : "") + (count && folder.kind !== "Drafts" ? " has-unread" : ""),
      href: "#mailbox=" + box.id + "&folder=" + folder.id,
      title: labelText,
      style: "padding-left:" + (1.5 + folder.depth * 0.9) + "rem"   // the gutter on the left holds the fold arrow of folders with subfolders
    }, [
      App.icon(KIND_ICONS[folder.kind] || "folder"),
      App.el("span", { class: "folder-item__name", text: labelText }),
      count ? App.el("span", { class: "folder-item__count", text: String(count) }) : null
    ]);

    if (tree.hasChildren) {
      var fold = App.iconButton(tree.folded ? "chevron-right" : "chevron-down", tree.folded ? T("expand") : T("collapse"), function (e) { e.preventDefault(); e.stopPropagation(); setFolded(folder, !tree.folded); }, "folder-item__fold");
      fold.style.left = (0.15 + folder.depth * 0.9) + "rem";
      node.appendChild(fold);
    }

    var more = App.iconButton("more-vertical", T("more"), function (e) { e.preventDefault(); e.stopPropagation(); folderMenu(more, box, folder); }, "folder-item__more");
    node.appendChild(more);
    node.addEventListener("contextmenu", function (e) { e.preventDefault(); folderMenu(node, box, folder); });

    // A folder is moved by dragging it onto another folder (or onto its mailbox: the top level), messages are moved by dragging them from the
    // list onto a folder (the menus do the same, also on touch screens).
    if (box.canManage && folder.kind === "Custom") {
      node.draggable = true;
      node.addEventListener("dragstart", function (e) { e.dataTransfer.setData("text/x-matmail-folder", String(folder.id)); e.dataTransfer.effectAllowed = "move"; dragged = { box: box, folder: folder }; doc.body.classList.add("is-dragging-folder"); });
      node.addEventListener("dragend", function () { dragged = null; clearDropMarks(); doc.body.classList.remove("is-dragging-folder"); });
    }
    if (box.canManage || box.canEdit) {
      node.addEventListener("dragover", function (e) {
        var accepts = draggedMessages ? canDropMessages(box, folder) : dragged && box.canManage && canDrop(dragged, box, folder);
        if (!accepts) { return; }
        e.preventDefault();
        e.dataTransfer.dropEffect = "move";
        node.classList.add("is-drop");
        if (draggedMessages && tree.folded) { unfoldSoon(folder); }   // hold the messages over a folded folder and it opens
      });
      node.addEventListener("dragleave", function () { node.classList.remove("is-drop"); cancelUnfold(); });
      node.addEventListener("drop", function (e) {
        if (draggedMessages && canDropMessages(box, folder)) {
          e.preventDefault();
          var drag = draggedMessages;
          draggedMessages = null;
          clearDropMarks();
          dropMessages(drag, folder);
          return;
        }
        if (!dragged || !box.canManage || !canDrop(dragged, box, folder)) { return; }
        e.preventDefault();
        var moving = dragged.folder;
        dragged = null;
        clearDropMarks();
        moveFolder(moving, folder.id);
      });
    }
    return node;
  }

  var dragged = null;          // the folder that is being dragged: { box, folder }
  var draggedMessages = null;  // the messages that are being dragged: { ids, box, everything }
  function clearDropMarks() { Array.prototype.forEach.call(doc.querySelectorAll(".folder-item.is-drop, .folder-group__head.is-drop"), function (n) { n.classList.remove("is-drop"); }); }

  /** The mailbox is the top of its tree: a folder dropped on it is moved to the top level (where it is already, nothing happens). */
  function makeRootTarget(node, box) {
    var accepts = function () { return !!dragged && box.canManage && dragged.box.id === box.id && !!dragged.folder.parentId; };
    node.addEventListener("dragover", function (e) {
      if (!accepts()) { return; }
      e.preventDefault();
      e.dataTransfer.dropEffect = "move";
      node.classList.add("is-drop");
    });
    node.addEventListener("dragleave", function () { node.classList.remove("is-drop"); });
    node.addEventListener("drop", function (e) {
      if (!accepts()) { return; }
      e.preventDefault();
      var moving = dragged.folder;
      dragged = null;
      clearDropMarks();
      moveFolder(moving, null);
    });
  }

  // ---- Messages dragged onto a folder --------------------------------------------------------
  /** A row is dragged: the ticked messages when it is one of them, else itself; as a file too, for the desktop (see dragOut). */
  function dragRow(e, item) {
    dragOut(e, item);
    var box = currentBox();
    if (!box || !box.canEdit) { return; }
    var picked = S.selected.has(item.id);
    var ids = picked ? Array.from(S.selected) : [item.id];
    draggedMessages = { ids: ids, box: box, everything: picked && S.allMatching };
    e.dataTransfer.setData("text/x-matmail-messages", ids.join(","));
    e.dataTransfer.effectAllowed = "copyMove";   // copy: to the desktop as a file, move: onto a folder
    var byId = {};
    S.items.forEach(function (i) { byId[i.id] = i; });
    var count = draggedMessages.everything ? S.total : expandRows(ids, byId).length;
    if (count > 1) {
      var ghost = App.el("div", { class: "drag-ghost", text: T("messagesCount").replace("{0}", String(count)) });
      doc.body.appendChild(ghost);
      e.dataTransfer.setDragImage(ghost, 14, 14);
      setTimeout(function () { ghost.remove(); }, 0);
    }
  }

  function endRowDrag() { draggedMessages = null; clearDropMarks(); cancelUnfold(); }

  /** Where the messages being dragged may go: any folder of a mailbox that may be changed, but not where they are, not Drafts and not the bucket of unclaimed mail. */
  function canDropMessages(targetBox, folder) {
    if (!draggedMessages || !targetBox.canEdit || folder.kind === "Drafts") { return false; }
    if (targetBox.id !== draggedMessages.box.id && targetBox.type === "Unassigned") { return false; }
    return !(targetBox.id === S.mailboxId && folder.id === S.folderId && !S.query);
  }

  function dropMessages(drag, folder) {
    if (drag.everything) { actSelected("move", folder.id); return; }
    act("move", drag.ids, folder.id);
  }

  var unfoldTimer = null, unfoldTarget = null;
  function unfoldSoon(folder) {
    if (unfoldTarget === folder.id) { return; }
    cancelUnfold();
    unfoldTarget = folder.id;
    unfoldTimer = setTimeout(function () { unfoldTimer = null; unfoldTarget = null; setFolded(folder, false); }, 700);
  }
  function cancelUnfold() {
    if (unfoldTimer) { clearTimeout(unfoldTimer); }
    unfoldTimer = null;
    unfoldTarget = null;
  }

  /** A folder may go below any folder of its own mailbox except itself, what is below it, and where it already is. */
  function canDrop(source, box, target) {
    if (source.box.id !== box.id || source.folder.id === target.id || source.folder.parentId === target.id) { return false; }
    return !isInside(box, source.folder, target);
  }

  /** True when <candidate> is the folder or one of the folders below it. */
  function isInside(box, folder, candidate) {
    var index = box.folders.indexOf(folder);
    if (index < 0) { return false; }
    return candidate.id === folder.id || subtreeOf(box, index).some(function (f) { return f.id === candidate.id; });
  }

  /** The menu of a mailbox (the three dots and the right button on its heading). */
  function mailboxMenu(anchor, box) {
    var items = [{ label: T("mailboxInfo"), icon: "info", onClick: function () { showMailboxInfo(box); } }];
    App.showMenu(anchor, items, { title: box.isOwn ? T("myMailbox") : box.name });
  }

  // ---------------------------------------------------------------------------------------------
  // Info dialog of a mailbox
  // ---------------------------------------------------------------------------------------------
  var ACCESS_TEXT = { Read: "accessRead", Edit: "accessEdit", Send: "accessSend", Manage: "accessManage" };
  var TYPE_TEXT = { Personal: "typePersonal", Shared: "typeShared", Unassigned: "typeUnassigned" };

  /** Who the mailbox is for, its addresses, and what it takes up – as a bar against its limit when it has one. */
  function showMailboxInfo(box) {
    App.get("/api/mail/mailboxes/" + box.id + "/info").then(function (info) {
      var body = App.el("div", { class: "mailbox-info" });

      var facts = App.el("dl", { class: "kv" });
      var fact = function (label, values) {
        values = [].concat(values).filter(function (v) { return v !== null && v !== undefined && v !== ""; });
        if (!values.length) { return; }
        facts.appendChild(App.el("dt", { text: label }));
        facts.appendChild(App.el("dd", null, values.map(function (v) { return App.el("div", { text: v }); })));
      };
      fact(T("mailboxType"), T(TYPE_TEXT[info.type] || "typePersonal"));
      fact(T("mailboxOwner"), info.owner);
      fact(T("mailboxAddresses"), info.addresses.map(function (a) { return a.indexOf("*@") === 0 ? a + " (" + T("catchAll") + ")" : a; }));
      fact(T("yourAccess"), T(ACCESS_TEXT[info.access] || "accessRead"));
      body.appendChild(facts);

      body.appendChild(App.el("h3", { class: "mailbox-info__heading", text: T("storage") }));
      body.appendChild(storageMeter(info));

      if (info.folders.length) {
        body.appendChild(App.el("h3", { class: "mailbox-info__heading", text: T("byFolder") }));
        var largest = info.folders[0].bytes || 1;
        var list = App.el("ul", { class: "folder-bars" });
        info.folders.slice(0, 8).forEach(function (f) {
          var label = f.kind === "Custom" ? f.path.split("/").join(" › ") : App.folderLabel({ kind: f.kind, name: f.name });
          list.appendChild(App.el("li", { class: "folder-bars__row" }, [
            App.el("span", { class: "folder-bars__name", text: label, title: label }),
            App.el("span", { class: "folder-bars__track" }, [App.el("span", { class: "folder-bars__fill", style: "width:" + Math.max(f.bytes ? 2 : 0, Math.round(f.bytes * 100 / largest)) + "%" })]),
            App.el("span", { class: "folder-bars__size", text: App.formatSize(f.bytes), title: T("storageMessages").replace("{0}", Number(f.messages).toLocaleString()) })
          ]));
        });
        body.appendChild(list);
        if (info.folders.length > 8) { body.appendChild(App.el("p", { class: "muted", text: T("moreFolders").replace("{0}", String(info.folders.length - 8)) })); }
      }

      App.infoDialog(info.name, T("mailboxInfoTitle"), body);
    }).catch(function (e) { App.toast(e.message, { error: true }); });
  }

  /** The storage as a bar (used against the limit: it turns amber when it is nearly full and red when it is) or, without a limit, as a number. */
  function storageMeter(info) {
    var wrap = App.el("div", { class: "meter" });
    var used = info.usedBytes, quota = info.quotaBytes;
    if (quota) {
      var level = usageLevel(used, quota), percent = level.percent;
      var bar = App.el("div", { class: "meter__bar" + (level.state ? " is-" + level.state : ""), role: "progressbar", "aria-valuemin": "0", "aria-valuemax": "100", "aria-valuenow": String(percent), "aria-label": T("storage") });
      bar.appendChild(App.el("div", { class: "meter__fill", style: "width:" + Math.max(used ? 1 : 0, percent) + "%" }));
      wrap.appendChild(bar);
      wrap.appendChild(App.el("div", { class: "meter__text" }, [
        App.el("strong", { text: T("storageOf").replace("{0}", App.formatSize(used)).replace("{1}", App.formatSize(quota)) }),
        App.el("span", { text: percent + " %" })
      ]));
      if (used >= quota) { wrap.appendChild(App.el("div", { class: "mail-notice is-error", text: T("storageFull") })); }
      // Deleting moves a message to the trash, where it still takes room: when space is short, say where it is.
      var trash = (info.folders || []).filter(function (f) { return f.kind === "Trash"; })[0];
      if (percent >= 90 && trash && trash.bytes) { wrap.appendChild(App.el("div", { class: "meter__more", text: T("storageTrash").replace("{0}", App.formatSize(trash.bytes)) })); }
    } else {
      wrap.appendChild(App.el("div", { class: "meter__text" }, [
        App.el("strong", { text: T("storageUsed").replace("{0}", App.formatSize(used)) }),
        App.el("span", { class: "muted", text: T("noLimit") })
      ]));
    }
    var more = [T("storageMessages").replace("{0}", Number(info.messages).toLocaleString())];
    if (info.remoteBytes) { more.push(T("storageRemote").replace("{0}", App.formatSize(info.remoteBytes))); }
    wrap.appendChild(App.el("div", { class: "meter__more muted", text: more.join(" · ") }));
    return wrap;
  }

  function folderMenu(anchor, box, folder) {
    var items = [];
    if (box.canEdit) { items.push({ label: T("markAllRead"), icon: "mail-open", onClick: function () { markFolderRead(folder); } }); }
    if (box.canEdit && (folder.kind === "Trash" || folder.kind === "Junk")) {
      items.push({ label: folder.kind === "Trash" ? T("emptyTrash") : T("emptySpam"), icon: "trash", danger: true, onClick: function () { emptyFolder(folder); } });
    }
    if (box.canManage && box.type !== "Unassigned") {
      if (items.length) { items.push({ divider: true }); }
      items.push({ label: T("newSubfolder"), icon: "folder-plus", onClick: function () { createFolder(box, folder); } });
    }
    if (box.canManage && folder.kind === "Custom") {
      items.push({ label: T("renameFolder"), icon: "edit", onClick: function () { renameFolder(folder); } });
      items.push({ label: T("moveFolder"), icon: "folder-input", onClick: function () { moveFolderMenu(anchor, box, folder); } });
      items.push({ divider: true });
      items.push({ label: T("deleteFolder"), icon: "trash", danger: true, onClick: function () { deleteFolder(folder); } });
    }
    if (items.length) { App.showMenu(anchor, items, { title: App.folderLabel(folder) }); }
  }

  /** Where could this folder go? The top level and every folder of the mailbox that is not the folder itself or inside it. */
  function moveFolderMenu(anchor, box, folder) {
    var atTop = !folder.parentId;
    var items = [{ label: T("topLevel"), icon: "inbox", checked: atTop, disabled: atTop, onClick: function () { moveFolder(folder, null); } }];
    box.folders.forEach(function (target) {
      if (isInside(box, folder, target)) { return; }
      var here = folder.parentId === target.id;
      items.push({ label: App.folderLabel(target), icon: KIND_ICONS[target.kind] || "folder", indent: target.depth, checked: here, disabled: here, onClick: function () { moveFolder(folder, target.id); } });
    });
    App.showMenu(anchor, items, { title: T("moveFolder") });
  }

  function moveFolder(folder, parentId) {
    App.post("/api/mail/folders/" + folder.id + "/move", { parentId: parentId }).then(function () {
      if (parentId !== null) { localStorage.removeItem("matmail-fold-" + parentId); }   // the folder that was moved is to be seen
      return refreshBootstrap();
    }).catch(function (e) { App.toast(e.message, { error: true }); });
  }

  function createFolder(box, parent) {
    var title = parent ? T("newSubfolder") + " – " + App.folderLabel(parent) : T("newFolder");
    App.promptDialog(title, "", T("folderName")).then(function (name) {
      if (!name) { return; }
      App.post("/api/mail/folders", { mailboxId: box.id, path: name, parentId: parent ? parent.id : null }).then(function () {
        if (parent) { localStorage.removeItem("matmail-fold-" + parent.id); }
        return refreshBootstrap();
      }).catch(function (e) { App.toast(e.message, { error: true }); });
    });
  }

  /** Renames the folder where it is; a name with a slash is a path from the top level (which also moves it). */
  function renameFolder(folder) {
    App.promptDialog(T("renameFolder"), folder.name, T("folderName")).then(function (name) {
      if (!name || name === folder.name) { return; }
      var parentPath = folder.path.length > folder.name.length ? folder.path.slice(0, folder.path.length - folder.name.length) : "";
      var path = name.indexOf("/") >= 0 ? name : parentPath + name;
      App.api("PATCH", "/api/mail/folders/" + folder.id, { path: path }).then(refreshBootstrap).catch(function (e) { App.toast(e.message, { error: true }); });
    });
  }

  function deleteFolder(folder) {
    App.confirm(T("confirmDeleteFolder")).then(function (ok) {
      if (!ok) { return; }
      App.api("DELETE", "/api/mail/folders/" + folder.id).then(function () {
        if (S.folderId === folder.id) { navigate({ folder: 0, page: 1, m: 0 }); }
        return refreshBootstrap();
      }).catch(function (e) { App.toast(e.message, { error: true }); });
    });
  }

  function markFolderRead(folder) {
    App.post("/api/mail/folders/" + folder.id + "/markread").then(function (result) {
      App.applyCounts(result.counts);
      if (S.folderId === folder.id) { loadList(true); }
    }).catch(function (e) { App.toast(e.message, { error: true }); });
  }

  function emptyFolder(folder) {
    App.confirm(T("confirmEmpty")).then(function (ok) {
      if (!ok) { return; }
      App.post("/api/mail/folders/" + folder.id + "/empty").then(function (result) {
        App.applyCounts(result.counts);
        if (S.folderId === folder.id) { loadList(true); }
      }).catch(function (e) { App.toast(e.message, { error: true }); });
    });
  }

  function refreshBootstrap() {
    return App.get("/api/mail/bootstrap").then(function (boot) { App.boot = boot; renderFolders(); route(); });
  }

  // ---------------------------------------------------------------------------------------------
  // List
  // ---------------------------------------------------------------------------------------------
  var listRequest = 0;
  function loadList(quiet) {
    var request = ++listRequest;
    if (!quiet) { showLoading(true); }
    var url = "/api/mail/messages?mailboxId=" + S.mailboxId + (S.folderId ? "&folderId=" + S.folderId : "") + "&page=" + S.page + (S.query ? "&q=" + encodeURIComponent(S.query) : "")
      + (conversationsOn() && S.folderId && !S.query ? "&conversations=true" : "");   // a search lists single messages
    return App.get(url).then(function (data) {
      if (request !== listRequest) { return; }
      showLoading(false);
      S.items = data.items;
      S.total = data.total;
      S.page = data.page;
      S.pageSize = data.pageSize;
      // Keep the selection of messages that are still there; "everything that matches" selects whatever the page shows.
      var ids = new Set(S.items.map(function (i) { return i.id; }));
      S.selected.forEach(function (id) { if (!ids.has(id)) { S.selected.delete(id); } });
      if (S.allMatching) {
        if (S.items.length) { S.selected = ids; } else { S.allMatching = false; }
      }
      S.cursor = Math.min(S.cursor, S.items.length - 1);
      renderListToolbar();
      renderList();
    }).catch(function (error) {
      if (request !== listRequest) { return; }
      showLoading(false);
      var lost = App.outbox.isOffline() || App.outbox.isNetworkError(error);
      showNotice(lost ? T("offlineList") : T("loadFailed") + " " + error.message, !lost);
    });
  }

  function showNotice(message, error) {
    els.notice.hidden = !message;
    els.notice.className = "mail-notice" + (error ? " is-error" : "");
    els.notice.textContent = message || "";
  }

  function currentFolder() { var hit = App.folder(S.folderId); return hit ? hit.folder : null; }
  function currentBox() { return App.mailbox(S.mailboxId); }

  function renderList() {
    showNotice("", false);
    els.list.innerHTML = "";
    var folder = currentFolder();
    var showTo = folder && (folder.kind === "Sent" || folder.kind === "Drafts");
    els.empty.hidden = S.items.length > 0;
    if (!S.items.length) {
      els.emptyText.textContent = S.query ? T("noResults") : folder ? emptyTextFor(folder) : T("nothingHere");
    }

    S.items.forEach(function (item, index) {
      var row = App.el("div", { class: "mail-row" + (item.isRead ? "" : " is-unread") + (S.selected.has(item.id) ? " is-selected" : "") + (index === S.cursor ? " is-cursor" : "") + (isSplit() && item.id === S.messageId ? " is-open" : ""), role: "listitem", "data-id": item.id, tabindex: "-1" });

      var check = App.el("input", { type: "checkbox", "aria-label": T("select") });
      check.checked = S.selected.has(item.id);
      check.addEventListener("click", function (e) { e.stopPropagation(); });
      check.addEventListener("change", function () { toggleSelect(item.id, check.checked, row); });
      row.appendChild(App.el("label", { class: "mail-row__check" }, [check]));

      var star = App.el("button", { type: "button", class: "mail-row__star" + (item.isStarred ? " is-on" : ""), title: T("star"), "aria-pressed": String(item.isStarred) });
      star.innerHTML = '<svg class="icon' + (item.isStarred ? " icon--filled" : "") + '" aria-hidden="true"><use href="#i-star"/></svg>';
      star.addEventListener("click", function (e) { e.stopPropagation(); toggleStar(item, star); });
      row.appendChild(star);

      var who = showTo ? (item.toSummary ? T("toPrefix") + " " + shortRecipients(item.toSummary) : T("noRecipient")) : App.displayName(item.fromName, item.fromAddress);
      if (item.isDraft && !showTo) { who = App.displayName(item.fromName, item.fromAddress); }
      // A conversation names the people who wrote and how many messages it has.
      if (item.participants && item.participants.length && !showTo) { who = item.participants.join(", "); }
      row.appendChild(App.el("div", { class: "mail-row__from" }, [
        App.el("span", { class: "mail-row__who", text: who }),
        item.ids && item.ids.length > 1 ? App.el("span", { class: "mail-row__count", text: String(item.ids.length), title: T("messagesCount").replace("{0}", item.ids.length) }) : null
      ]));

      var main = App.el("div", { class: "mail-row__main" }, [
        App.el("span", { class: "mail-row__subject", text: item.subject || T("noSubject") }),
        item.snippet ? App.el("span", { class: "mail-row__snippet", text: " – " + item.snippet }) : null
      ]);
      row.appendChild(main);

      row.appendChild(App.el("span", { class: "mail-row__attach" }, [item.hasAttachments ? App.icon("paperclip") : null]));
      row.appendChild(App.el("div", { class: "mail-row__date", text: App.formatListDate(item.date), title: App.formatFullDate(item.date) }));

      var actions = App.el("div", { class: "mail-row__actions" });
      var box = currentBox();
      if (box && box.canEdit) {
        if (folder && folder.kind !== "Archive" && folder.kind !== "Trash") { actions.appendChild(App.iconButton("archive", T("archive"), function (e) { e.stopPropagation(); act("archive", [item.id]); })); }
        actions.appendChild(App.iconButton("trash", T("delete"), function (e) { e.stopPropagation(); act("delete", [item.id]); }));
        actions.appendChild(App.iconButton(item.isRead ? "mail" : "mail-open", item.isRead ? T("markUnread") : T("markRead"), function (e) { e.stopPropagation(); act(item.isRead ? "unread" : "read", [item.id]); }));
      }
      row.appendChild(actions);

      row.draggable = true;
      row.addEventListener("dragstart", function (e) { dragRow(e, item); });
      row.addEventListener("dragend", endRowDrag);
      row.addEventListener("click", function () { S.cursor = index; openItem(item); });
      row.addEventListener("keydown", function (e) { if (e.key === "Enter") { openItem(item); } });
      els.list.appendChild(row);
    });
  }

  function shortRecipients(summary) {
    var first = summary.split(",")[0].replace(/<[^>]*>/g, "").trim();
    var more = summary.split(",").length - 1;
    return first + (more > 0 ? " +" + more : "");
  }

  function emptyTextFor(folder) {
    return { Inbox: T("emptyInbox"), Sent: T("emptySent"), Drafts: T("emptyDrafts"), Trash: T("emptyTrashText"), Junk: T("emptySpamText"), Archive: T("emptyArchive") }[folder.kind] || T("nothingHere");
  }

  function openItem(item) {
    if (item.isDraft) { App.compose.openDraft(item.id); return; }
    navigate({ m: item.id });
  }

  function toggleSelect(id, on, row) {
    S.allMatching = false;
    if (on) { S.selected.add(id); } else { S.selected.delete(id); }
    if (row) { row.classList.toggle("is-selected", on); }
    renderListToolbar();
  }

  function toggleStar(item, button) {
    item.isStarred = !item.isStarred;
    button.classList.toggle("is-on", item.isStarred);
    button.setAttribute("aria-pressed", String(item.isStarred));
    button.querySelector("svg").classList.toggle("icon--filled", item.isStarred);
    var ids = !item.isStarred && item.ids ? item.ids : [item.id];   // taking the star off a conversation takes it off all its messages
    App.post("/api/mail/messages/flags", { ids: ids, isStarred: item.isStarred }).catch(function (e) { App.toast(e.message, { error: true }); loadList(true); });
  }

  // ---------------------------------------------------------------------------------------------
  // Toolbars
  // ---------------------------------------------------------------------------------------------
  function renderListToolbar() {
    var bar = els.listToolbar;
    bar.innerHTML = "";
    var folder = currentFolder();
    var box = currentBox();
    var count = S.selected.size;

    var selectAll = App.el("input", { type: "checkbox", "aria-label": T("selectAll") });
    selectAll.checked = S.allMatching || (count > 0 && count === S.items.length);
    selectAll.indeterminate = !S.allMatching && count > 0 && count < S.items.length;
    selectAll.addEventListener("change", function () {
      S.allMatching = false;
      S.selected = new Set(selectAll.checked ? S.items.map(function (i) { return i.id; }) : []);
      renderList(); renderListToolbar();
    });
    var selectMenu = App.iconButton("chevron-down", T("select"), function (e) {
      App.showMenu(e.currentTarget, [
        { label: T("all"), onClick: function () { selectWhere(function () { return true; }); } },
        { label: T("none"), onClick: function () { selectWhere(function () { return false; }); } },
        { label: T("read"), onClick: function (i) { selectWhere(function (m) { return m.isRead; }); } },
        { label: T("unread"), onClick: function () { selectWhere(function (m) { return !m.isRead; }); } },
        { label: T("starred"), onClick: function () { selectWhere(function (m) { return m.isStarred; }); } },
        { label: T("notStarred"), onClick: function () { selectWhere(function (m) { return !m.isStarred; }); } }
      ], { title: T("select") });
    }, "toolbar-select");
    bar.appendChild(App.el("div", { class: "mail-toolbar__select" }, [App.el("label", { class: "mail-toolbar__check" }, [selectAll]), selectMenu]));

    if (count === 0) {
      bar.appendChild(App.iconButton("refresh", T("refresh"), function () { loadList(false); refreshCounts(); }));
      bar.appendChild(App.iconButton("upload", T("openMessageFile"), function () { els.file.click(); }));
      if (box && box.canEdit && folder) {
        var more = App.iconButton("more-vertical", T("more"), function (e) { folderMenu(e.currentTarget, box, folder); });
        bar.appendChild(more);
      }
    } else {
      // A search lists several folders (folder is null then); archive and spam make sense for those as well.
      var kind = folder ? folder.kind : "";
      if (box && box.canEdit) {
        if (kind !== "Archive" && kind !== "Trash") { bar.appendChild(App.iconButton("archive", T("archive"), function () { actSelected("archive"); })); }
        if (kind === "Junk") { bar.appendChild(App.iconButton("inbox", T("notSpam"), function () { actSelected("notspam"); })); }
        else if (kind !== "Trash") { bar.appendChild(App.iconButton("spam", T("reportSpam"), function () { actSelected("spam"); })); }
        bar.appendChild(App.iconButton("trash", kind === "Trash" ? T("deleteForever") : T("delete"), function () { actSelected("delete"); }));
        bar.appendChild(App.el("span", { class: "mail-toolbar__sep" }));
        bar.appendChild(App.iconButton("mail-open", T("markRead"), function () { actSelected("read"); }));
        bar.appendChild(App.iconButton("mail", T("markUnread"), function () { actSelected("unread"); }));
        bar.appendChild(App.iconButton("folder-input", T("moveTo"), function (e) { moveMenu(e.currentTarget, Array.from(S.selected), S.allMatching); }));
        bar.appendChild(App.iconButton("more-vertical", T("more"), function (e) {
          App.showMenu(e.currentTarget, [
            { label: T("addStar"), icon: "star", onClick: function () { actSelected("star"); } },
            { label: T("removeStar"), icon: "star", onClick: function () { actSelected("unstar"); } }
          ]);
        }));
      }
      bar.appendChild(App.el("span", { class: "mail-toolbar__count", text: (S.allMatching ? T("allSelectedCount").replace("{0}", S.total.toLocaleString()) : T("selectedCount").replace("{0}", count)) }));
    }

    bar.appendChild(App.el("span", { class: "mail-toolbar__spacer" }));
    bar.appendChild(pager());
    renderSelectBar();
  }

  // ---------------------------------------------------------------------------------------------
  // "Select everything that matches" (like Gmail): the whole page is selected and there are more hits than the page shows
  // ---------------------------------------------------------------------------------------------
  function renderSelectBar() {
    var bar = els.selectBar;
    bar.innerHTML = "";
    var offer = !S.allMatching && S.items.length > 0 && S.selected.size === S.items.length && S.total > S.items.length;
    bar.hidden = !(offer || S.allMatching);
    if (bar.hidden) { return; }
    if (S.allMatching) {
      bar.appendChild(App.el("span", { text: T("allMatchingSelected").replace("{0}", S.total.toLocaleString()) }));
      var clear = App.el("button", { type: "button", class: "mail-selectbar__link", text: T("clearSelection") });
      clear.addEventListener("click", function () { S.allMatching = false; S.selected.clear(); renderList(); renderListToolbar(); });
      bar.appendChild(clear);
    } else {
      bar.appendChild(App.el("span", { text: T("pageSelected").replace("{0}", S.items.length.toLocaleString()) }));
      var all = App.el("button", { type: "button", class: "mail-selectbar__link", text: T(S.query ? "selectAllMatching" : "selectAllInFolder").replace("{0}", S.total.toLocaleString()) });
      all.addEventListener("click", function () { S.allMatching = true; renderListToolbar(); });
      bar.appendChild(all);
    }
  }

  var BULK_CHUNK = 500;   // the server changes at most a thousand messages per call

  /** The action for what is selected: the messages on the screen, or everything the list matches over all its pages. */
  function actSelected(kind, targetFolderId) {
    if (!S.allMatching) { act(kind, Array.from(S.selected), targetFolderId); return; }
    var folder = currentFolder();
    var gentle = kind === "read" || kind === "unread" || kind === "star" || kind === "unstar";
    var question = kind === "delete" && folder && folder.kind === "Trash" ? T("confirmDeleteForeverAll") : T("confirmAllMatching");
    (gentle ? Promise.resolve(true) : App.confirm(question.replace("{0}", S.total.toLocaleString()))).then(function (ok) {
      if (!ok) { return; }
      showLoading(true);
      var url = "/api/mail/messages/ids?mailboxId=" + S.mailboxId + (S.folderId ? "&folderId=" + S.folderId : "") + (S.query ? "&q=" + encodeURIComponent(S.query) : "");
      return App.get(url).then(function (all) {
        showLoading(false);
        if (all.capped) { App.toast(T("onlyNewest").replace("{0}", all.ids.length.toLocaleString())); }
        var origin = all.ids.map(function (id, i) { return { id: id, folderId: all.folderIds[i] }; });
        act(kind, all.ids, targetFolderId, { origin: origin, confirmed: true });
      });
    }).catch(function (e) { showLoading(false); App.toast(e.message, { error: true }); });
  }

  /** Posts the ids in chunks (one call when there are few) and adds the answers up. */
  function postIds(url, ids, extra) {
    var chunks = [];
    for (var i = 0; i < ids.length; i += BULK_CHUNK) { chunks.push(ids.slice(i, i + BULK_CHUNK)); }
    var merged = { changed: 0, counts: {} };
    return chunks.reduce(function (previous, chunk) {
      return previous.then(function () {
        return App.post(url, Object.assign({ ids: chunk }, extra)).then(function (result) {
          merged.changed += result.changed || 0;
          Object.keys(result.counts || {}).forEach(function (folderId) { merged.counts[folderId] = result.counts[folderId]; });
        });
      });
    }, Promise.resolve()).then(function () { return merged; });
  }

  function selectWhere(predicate) {
    S.allMatching = false;
    S.selected = new Set(S.items.filter(predicate).map(function (i) { return i.id; }));
    renderList(); renderListToolbar();
  }

  function pager() {
    var wrap = App.el("div", { class: "mail-pager" });
    if (!S.total) { return wrap; }
    var first = (S.page - 1) * S.pageSize + 1, last = Math.min(S.total, S.page * S.pageSize);
    wrap.appendChild(App.el("span", { class: "mail-pager__text", text: T("pagerText").replace("{0}", first).replace("{1}", last).replace("{2}", S.total.toLocaleString()) }));
    var prev = App.iconButton("chevron-left", T("newer"), function () { navigate({ page: S.page - 1 }); });
    var next = App.iconButton("chevron-right", T("older"), function () { navigate({ page: S.page + 1 }); });
    prev.disabled = S.page <= 1;
    next.disabled = last >= S.total;
    wrap.appendChild(prev);
    wrap.appendChild(next);
    return wrap;
  }

  /** everything: move whatever the list matches (see actSelected) instead of the given ids. */
  function moveMenu(anchor, ids, everything, origin) {
    var box = currentBox();
    var items = [];
    var run = function (target) { if (everything) { actSelected("move", target); } else { act("move", ids, target, origin ? { origin: origin } : undefined); } };
    box.folders.forEach(function (f) {
      if (f.id !== S.folderId) { items.push({ label: App.folderLabel(f), icon: KIND_ICONS[f.kind] || "folder", indent: f.depth, onClick: function () { run(f.id); } }); }
    });
    if (box.type === "Unassigned" || App.boot.mailboxes.length > 1) {
      items.push({ divider: true });
      App.boot.mailboxes.forEach(function (other) {
        if (other.id === box.id || !other.canEdit) { return; }
        var inbox = other.folders.filter(function (f) { return f.kind === "Inbox"; })[0];
        if (inbox) { items.push({ label: other.name + " – " + T("inbox"), icon: "inbox", onClick: function () { run(inbox.id); } }); }
      });
    }
    App.showMenu(anchor, items, { title: T("moveTo") });
  }

  // ---------------------------------------------------------------------------------------------
  // Actions on messages (list and reader)
  // ---------------------------------------------------------------------------------------------
  /** options: { origin: where the messages were (when the caller knows), confirmed: a question was asked already }. */
  function act(kind, ids, targetFolderId, options) {
    if (!ids.length) { return; }
    options = options || {};
    var byId = {};
    S.items.forEach(function (i) { byId[i.id] = i; });
    ids = expandRows(ids, byId);
    var origin = options.origin || ids.map(function (id) { return { id: id, folderId: byId[id] ? byId[id].folderId : S.folderId }; });
    var request, undo = null, message = null, removes = false;
    var folder = currentFolder();

    switch (kind) {
      case "archive": var archive = App.folderByKind(S.mailboxId, "Archive"); if (!archive) { return; } request = postIds("/api/mail/messages/move", ids, { folderId: archive.id }); message = T("movedToArchive"); undo = true; removes = true; break;
      case "move": request = postIds("/api/mail/messages/move", ids, { folderId: targetFolderId }); message = T("moved"); undo = true; removes = true; break;
      case "delete":
        if (folder && folder.kind === "Trash") {
          var forever = function () { send(postIds("/api/mail/messages/delete", ids, { permanent: true }), T("deletedForever"), false, true); };
          if (options.confirmed) { forever(); } else { App.confirm(T("confirmDeleteForever")).then(function (ok) { if (ok) { forever(); } }); }
          return;
        }
        request = postIds("/api/mail/messages/delete", ids, { permanent: false }); message = T("movedToTrash"); undo = true; removes = true; break;
      case "spam": request = postIds("/api/mail/messages/spam", ids, { notSpam: false }); message = T("movedToSpam"); undo = true; removes = true; break;
      case "notspam": request = postIds("/api/mail/messages/spam", ids, { notSpam: true }); message = T("movedToInbox"); removes = true; break;
      case "read": request = postIds("/api/mail/messages/flags", ids, { isRead: true }); patch(ids, { isRead: true }); break;
      case "unread": request = postIds("/api/mail/messages/flags", ids, { isRead: false }); patch(ids, { isRead: false }); break;
      case "star": request = postIds("/api/mail/messages/flags", ids, { isStarred: true }); patch(ids, { isStarred: true }); break;
      case "unstar": request = postIds("/api/mail/messages/flags", ids, { isStarred: false }); patch(ids, { isStarred: false }); break;
      default: return;
    }

    send(request, message, undo, removes, origin);
  }

  /** A row of a list of conversations stands for all the messages of its conversation in that list: they are what an action changes. */
  function expandRows(ids, byId) {
    var all = [];
    ids.forEach(function (id) {
      var row = byId[id];
      (row && row.ids ? row.ids : [id]).forEach(function (x) { if (all.indexOf(x) < 0) { all.push(x); } });
    });
    return all;
  }

  function send(request, message, undo, removes, origin) {
    request.then(function (result) {
      App.applyCounts(result.counts);
      if (removes) {
        S.selected.clear();
        S.allMatching = false;
        if (S.messageId) { navigate({ m: 0 }); if (isSplit()) { loadList(true); } } else { loadList(true); }
      } else {
        renderList(); renderListToolbar();
      }
      if (message) {
        var toastOptions = {};
        if (undo && origin) { toastOptions.action = { label: T("undo"), run: function () { undoMove(origin); } }; }
        App.toast(message, toastOptions);
      }
    }).catch(function (e) { App.toast(e.message, { error: true }); loadList(true); });
  }

  function undoMove(origin) {
    var byFolder = {};
    origin.forEach(function (o) { (byFolder[o.folderId] = byFolder[o.folderId] || []).push(o.id); });
    Promise.all(Object.keys(byFolder).map(function (folderId) {
      return postIds("/api/mail/messages/move", byFolder[folderId], { folderId: parseInt(folderId, 10) });
    })).then(function (results) {
      results.forEach(function (r) { App.applyCounts(r.counts); });
      loadList(true);
    }).catch(function (e) { App.toast(e.message, { error: true }); });
  }

  function patch(ids, changes) {
    var set = new Set(ids);
    var wasUnread = 0;
    S.items.forEach(function (item) {
      var members = item.ids || [item.id];
      var hit = members.filter(function (id) { return set.has(id); }).length;
      if (!hit) { return; }
      if (changes.isRead !== undefined) {
        // A row of a conversation is read when none of its messages is unread.
        var before = item.ids ? item.unreadCount : (item.isRead ? 0 : 1);
        var after = changes.isRead ? Math.max(0, before - hit) : Math.min(members.length, before + hit);
        wasUnread += after - before;
        if (item.ids) { item.unreadCount = after; }
        item.isRead = after === 0;
      }
      Object.keys(changes).forEach(function (k) { if (k !== "isRead") { item[k] = changes[k]; } });
    });
    var folder = currentFolder();
    if (folder && wasUnread) { folder.unread = Math.max(0, folder.unread + wasUnread); renderFolders(); }
    if (isSplit()) { renderList(); }   // the list is on the screen next to the reader
  }

  function refreshCounts() {
    App.boot.mailboxes.forEach(function (box) {
      App.get("/api/mail/counts?mailboxId=" + box.id).then(App.applyCounts).catch(function () { /* the next refresh will do */ });
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Message files: dropped into the client (or chosen with the button) to be read; dragged out of the list to be kept
  // ---------------------------------------------------------------------------------------------
  /** A file dropped anywhere on the client is opened (not in the compose window: there it is an attachment). */
  function setupFileDrops() {
    var depth = 0;
    var hasFiles = function (e) { return !!e.dataTransfer && Array.prototype.indexOf.call(e.dataTransfer.types || [], "Files") >= 0; };
    var foreign = function (e) { return !!(e.target && e.target.closest && e.target.closest(".compose, dialog[open]")); };
    doc.addEventListener("dragenter", function (e) { if (hasFiles(e) && !foreign(e)) { depth++; els.drop.hidden = false; } });
    doc.addEventListener("dragover", function (e) { if (hasFiles(e) && !foreign(e)) { e.preventDefault(); e.dataTransfer.dropEffect = "copy"; } });
    doc.addEventListener("dragleave", function (e) { if (hasFiles(e)) { depth = Math.max(0, depth - 1); if (!depth) { els.drop.hidden = true; } } });
    doc.addEventListener("drop", function (e) {
      depth = 0;
      els.drop.hidden = true;
      if (!hasFiles(e) || foreign(e)) { return; }
      e.preventDefault();
      if (e.dataTransfer.files.length) { openFile(e.dataTransfer.files[0]); }
    });
    els.file.addEventListener("change", function () {
      if (els.file.files.length) { openFile(els.file.files[0]); }
      els.file.value = "";
    });
  }

  /** The file goes to the server, which reads it (an .eml as it is, an Outlook .msg turned into a message) and keeps it for a day. */
  function openFile(file) {
    if (!/\.(eml|msg)$/i.test(file.name)) { App.toast(T("onlyMessageFiles"), { error: true }); return; }
    showLoading(true);
    App.api("POST", "/api/mail/preview", file, { headers: { "X-File-Name": encodeURIComponent(file.name) } }).then(function (preview) {
      showLoading(false);
      S.preview = preview;
      navigate({ p: preview.id, m: 0 });
    }).catch(function (error) {
      showLoading(false);
      App.toast(error.message, { error: true });
    });
  }

  function loadPreview(id) {
    var request = ++messageRequest;
    els.reader.innerHTML = "";
    els.readerToolbar.innerHTML = "";
    var cached = S.preview && S.preview.id === id ? Promise.resolve(S.preview) : (showLoading(true), App.get("/api/mail/preview/" + id));
    cached.then(function (preview) {
      if (request !== messageRequest) { return; }
      showLoading(false);
      S.preview = preview;
      renderPreview(preview);
    }).catch(function (error) {
      if (request !== messageRequest) { return; }
      showLoading(false);
      els.reader.appendChild(App.el("div", { class: "mail-notice is-error", text: error.status === 404 ? T("fileGone") : error.message }));
      els.readerToolbar.appendChild(App.iconButton("arrow-left", T("back"), function () { navigate({ m: 0 }); }, "reader-back"));
    });
  }

  /** A message from a file: shown like one of the mailbox, without what belongs to a mailbox (stars, answers), with a way to keep it. */
  function renderPreview(preview) {
    var m = preview.message;
    var bar = els.readerToolbar;
    bar.innerHTML = "";
    bar.appendChild(App.iconButton("arrow-left", T("back"), function () { navigate({ m: 0 }); }, "reader-back"));
    var canSave = App.boot.mailboxes.some(function (box) { return box.canEdit; });
    var saveButton = null;
    if (canSave) {
      saveButton = App.el("button", { type: "button", class: "btn btn--secondary btn--sm mail-toolbar__button" }, [App.icon("folder-input"), App.el("span", { text: T("saveToFolder") })]);
      saveButton.addEventListener("click", function (e) { saveMenu(e.currentTarget, preview); });
      bar.appendChild(saveButton);
    }
    bar.appendChild(App.iconButton("download", T("downloadEml"), function () { window.location.href = "/api/mail/preview/" + preview.id + "/raw"; }));
    bar.appendChild(App.el("span", { class: "mail-toolbar__spacer" }));

    var wrap = App.el("div", { class: "reader" });
    var head = readerHead(m.subject, 0, 0);
    head.appendChild(App.el("span", { class: "chip", text: preview.fileName }));
    wrap.appendChild(head);
    wrap.appendChild(App.el("div", { class: "reader__banner" }, [App.icon("file-text"), App.el("span", { text: T("fileNotice") })]));
    wrap.appendChild(messageView(m, null, { preview: true }));
    els.reader.innerHTML = "";
    els.reader.appendChild(wrap);
    els.reader.scrollTop = 0;
  }

  /** The folders (of every mailbox one may change) to keep a message file in. */
  function saveMenu(anchor, preview) {
    var items = [];
    var several = App.boot.mailboxes.filter(function (box) { return box.canEdit; }).length > 1;
    App.boot.mailboxes.forEach(function (box) {
      if (!box.canEdit) { return; }
      box.folders.forEach(function (folder) {
        if (folder.kind === "Drafts") { return; }
        items.push({ label: (several ? box.name + " – " : "") + App.folderLabel(folder), icon: KIND_ICONS[folder.kind] || "folder", indent: folder.depth, onClick: function () { keepFile(preview, box, folder); } });
      });
    });
    App.showMenu(anchor, items, { title: T("saveToFolder") });
  }

  function keepFile(preview, box, folder) {
    showLoading(true);
    App.post("/api/mail/preview/" + preview.id + "/import", { folderId: folder.id }).then(function (result) {
      showLoading(false);
      App.applyCounts(result.counts);
      if (S.folderId === folder.id) { loadList(true); }
      App.toast(T("savedTo").replace("{0}", App.folderLabel(folder)), {
        action: { label: T("open"), run: function () { navigate({ mailbox: box.id, folder: folder.id, page: 1, q: "", m: result.id }); } }
      });
    }).catch(function (error) {
      showLoading(false);
      App.toast(error.message, { error: true });
    });
  }

  /**
   * A row dragged out of the client becomes a file of its own (Chrome and Edge: the DownloadURL of the drag makes the browser fetch the
   * .eml with the session of the page, to the desktop or into a folder of the file manager).
   */
  function dragOut(e, item) {
    var name = (item.subject || "message").replace(/[\\/:*?"<>|\r\n]+/g, " ").replace(/\s+/g, " ").trim().slice(0, 80) || "message";
    var url = location.origin + "/api/mail/messages/" + item.id + "/raw";
    e.dataTransfer.effectAllowed = "copy";
    e.dataTransfer.setData("DownloadURL", "message/rfc822:" + name + ".eml:" + url);
    e.dataTransfer.setData("text/uri-list", url);
  }

  // ---------------------------------------------------------------------------------------------
  // Reader
  // ---------------------------------------------------------------------------------------------
  var messageRequest = 0;

  /** The conversations of the appearance settings: the reader stacks the messages of a thread. */
  function conversationsOn() { return doc.documentElement.getAttribute("data-conversations") === "on"; }

  function loadMessage(id) {
    var request = ++messageRequest;
    els.reader.innerHTML = "";
    els.readerToolbar.innerHTML = "";
    showLoading(true);
    var detail = App.get("/api/mail/messages/" + id);
    // Where conversations are on, the thread is asked for along with the message (without it, the message is shown alone).
    var thread = conversationsOn() ? App.get("/api/mail/messages/" + id + "/thread").catch(function () { return []; }) : Promise.resolve([]);
    Promise.all([detail, thread]).then(function (results) {
      if (request !== messageRequest) { return; }
      showLoading(false);
      var message = results[0], items = results[1];
      S.current = message;
      if (message.isDraft) { App.compose.openDraft(id); navigate({ m: 0 }); return; }
      if (items.length > 1) { renderConversation(message, items); return; }
      renderReader(message);
      markRead(message);
    }).catch(function (error) {
      if (request !== messageRequest) { return; }
      showLoading(false);
      els.reader.appendChild(App.el("div", { class: "mail-notice is-error", text: error.status === 404 ? T("messageGone") : error.message }));
      els.readerToolbar.appendChild(App.iconButton("arrow-left", T("back"), function () { navigate({ m: 0 }); }, "reader-back"));
    });
  }

  /** A message that is opened is read: the server and the list are told, when it was unread and may be changed. */
  function markRead(message) {
    if (message.isRead || !message.canEdit) { return; }
    message.isRead = true;
    App.post("/api/mail/messages/flags", { ids: [message.id], isRead: true })
      .then(function (result) { App.applyCounts(result.counts); patch([message.id], { isRead: true }); })
      .catch(function () { message.isRead = false; /* stays unread */ });
  }

  function addrLabel(a) { return a ? (a.name ? a.name + " <" + a.address + ">" : a.address) : ""; }

  /** What the buttons over the reader change: the message, or the messages of its conversation that are in its folder. */
  function scopeOf(m, items) {
    var inFolder = (items || []).filter(function (i) { return i.folderId === m.folderId; });
    if (!inFolder.length) { inFolder = [{ id: m.id, folderId: m.folderId }]; }
    return {
      ids: inFolder.map(function (i) { return i.id; }),
      origin: inFolder.map(function (i) { return { id: i.id, folderId: i.folderId }; })
    };
  }

  function renderReaderToolbar(m, scope) {
    var bar = els.readerToolbar;
    bar.innerHTML = "";
    bar.appendChild(App.iconButton("arrow-left", T("back"), function () { navigate({ m: 0 }); }, "reader-back"));
    var ids = scope.ids, options = { origin: scope.origin };
    if (m.canEdit) {
      if (m.folderKind !== "Archive" && m.folderKind !== "Trash") { bar.appendChild(App.iconButton("archive", T("archive"), function () { act("archive", ids, undefined, options); })); }
      if (m.folderKind === "Junk") { bar.appendChild(App.iconButton("inbox", T("notSpam"), function () { act("notspam", ids, undefined, options); })); }
      else if (m.folderKind !== "Trash") { bar.appendChild(App.iconButton("spam", T("reportSpam"), function () { act("spam", ids, undefined, options); })); }
      bar.appendChild(App.iconButton("trash", m.folderKind === "Trash" ? T("deleteForever") : T("delete"), function () { act("delete", ids, undefined, options); }));
      bar.appendChild(App.el("span", { class: "mail-toolbar__sep" }));
      bar.appendChild(App.iconButton("mail", T("markUnread"), function () { App.post("/api/mail/messages/flags", { ids: ids, isRead: false }).then(function (r) { App.applyCounts(r.counts); navigate({ m: 0 }); }); }));
      bar.appendChild(App.iconButton("folder-input", T("moveTo"), function (e) { moveMenu(e.currentTarget, ids, false, scope.origin); }));
    }
    bar.appendChild(App.el("span", { class: "mail-toolbar__spacer" }));
    bar.appendChild(App.iconButton("more-vertical", T("more"), function (e) {
      var items = [{ label: T("print"), icon: "printer", onClick: function () { printMessage(m); } }];
      if (canShare()) { items.push({ label: T("share"), icon: "share", onClick: function () { shareMessage(m); } }); }
      items.push(
        { label: T("downloadEml"), icon: "download", onClick: function () { window.location.href = "/api/mail/messages/" + m.id + "/raw"; } },
        { label: T("showOriginal"), icon: "file-text", onClick: function () { window.open("/api/mail/messages/" + m.id + "/raw", "_blank"); } }
      );
      App.showMenu(e.currentTarget, items, { alignRight: true });
    }));
  }

  /** The subject over the reader, with the folder the message is in and, for a conversation, how many messages it has. */
  function readerHead(subject, folderId, count) {
    var row = App.el("div", { class: "reader__head" }, [App.el("h1", { class: "reader__subject", text: subject || T("noSubject") })]);
    var hit = App.folder(folderId);
    if (hit) { row.appendChild(App.el("span", { class: "chip", text: App.folderLabel(hit.folder) })); }
    if (count > 1) { row.appendChild(App.el("span", { class: "chip", text: T("messagesCount").replace("{0}", count) })); }
    return row;
  }

  function renderReader(m) {
    renderReaderToolbar(m, scopeOf(m, null));
    var wrap = App.el("div", { class: "reader" });
    wrap.appendChild(readerHead(m.subject, m.folderId, 0));
    wrap.appendChild(messageView(m));
    els.reader.innerHTML = "";
    els.reader.appendChild(wrap);
    els.reader.scrollTop = 0;
  }

  /** "Re: Re: Offer" is the conversation "Offer". */
  function withoutReplyPrefix(subject) {
    var plain = (subject || "").replace(/^\s*((re|aw|antw|wg|fw|fwd|sv|vs|tr|rv)(\[\d+\])?\s*:\s*)+/i, "").trim();
    return plain || subject;
  }

  /** The messages of a conversation as a stack: the newest and the unread ones open, the others closed to a line each (a click opens them). */
  function renderConversation(anchor, items) {
    renderReaderToolbar(anchor, scopeOf(anchor, items));
    var wrap = App.el("div", { class: "reader thread" });
    wrap.appendChild(readerHead(withoutReplyPrefix(anchor.subject), anchor.folderId, items.length));
    var first = null;
    items.forEach(function (item) {
      var open = item.id === anchor.id || !item.isRead;
      var card = threadCard(item, anchor, open);
      if (!first && open && !item.isRead) { first = card; }
      wrap.appendChild(card);
    });
    els.reader.innerHTML = "";
    els.reader.appendChild(wrap);
    // The older messages above are closed and short: the first unread one (else the newest) is brought to the top.
    var target = first || wrap.querySelector('.thread__msg[data-id="' + anchor.id + '"]');
    els.reader.scrollTop = target ? Math.max(0, target.offsetTop - 12) : 0;
  }

  function threadCard(item, anchor, open) {
    var card = App.el("div", { class: "thread__msg is-collapsed", "data-id": item.id });
    var line = App.el("button", { type: "button", class: "thread__line" + (item.isRead ? "" : " is-unread"), "aria-expanded": "false" }, [
      App.avatar(item.fromName, item.fromAddress),
      App.el("span", { class: "thread__from", text: App.displayName(item.fromName, item.fromAddress) }),
      App.el("span", { class: "thread__snippet", text: item.snippet || "" }),
      item.hasAttachments ? App.icon("paperclip") : null,
      App.el("span", { class: "thread__date", text: App.formatListDate(item.date), title: App.formatFullDate(item.date) })
    ]);
    var body = App.el("div", { class: "thread__body" });
    card.appendChild(line);
    card.appendChild(body);

    var loaded = false;
    var close = function () { card.classList.add("is-collapsed"); line.setAttribute("aria-expanded", "false"); };
    var show = function (m) {
      body.innerHTML = "";
      body.appendChild(messageView(m, close));
      markRead(m);
      line.classList.remove("is-unread");   // it was opened: when it is closed again it reads as read
    };
    var openCard = function () {
      card.classList.remove("is-collapsed");
      line.setAttribute("aria-expanded", "true");
      if (loaded) { return; }
      loaded = true;
      if (item.id === anchor.id) { show(anchor); return; }
      body.appendChild(App.el("div", { class: "thread__loading" }, [App.el("span", { class: "spinner" })]));
      App.get("/api/mail/messages/" + item.id).then(function (m) { if (card.isConnected) { show(m); } }).catch(function (error) {
        loaded = false;
        body.innerHTML = "";
        body.appendChild(App.el("div", { class: "mail-notice is-error", text: error.status === 404 ? T("messageGone") : error.message }));
      });
    };
    line.addEventListener("click", openCard);
    if (open) { openCard(); }
    return card;
  }

  /** One message: sender, details, the text in its frame, attachments, the buttons to answer. onCollapse: a button that closes it again (in a conversation). */
  function messageView(m, onCollapse, options) {
    var wrap = App.el("div", { class: "reader__message" });

    // Sender line
    var from = m.from || { name: "", address: "" };
    var toLine = m.to.length ? m.to.map(function (a) { return a.address === (App.boot.identities[0] || {}).address ? T("me") : App.displayName(a.name, a.address); }).join(", ") : "";
    var details = App.el("div", { class: "reader__details", hidden: true }, [
      detailRow(T("from"), addrLabel(from)),
      m.replyTo.length ? detailRow(T("replyTo"), m.replyTo.map(addrLabel).join(", ")) : null,
      detailRow(T("to"), m.to.map(addrLabel).join(", ")),
      m.cc.length ? detailRow(T("cc"), m.cc.map(addrLabel).join(", ")) : null,
      m.bcc.length ? detailRow(T("bcc"), m.bcc.map(addrLabel).join(", ")) : null,
      detailRow(T("date"), App.formatFullDate(m.date)),
      m.envelopeRecipients ? detailRow(T("deliveredFor"), m.envelopeRecipients) : null
    ]);
    var toggle = App.el("button", { type: "button", class: "reader__to", "aria-expanded": "false" }, [App.el("span", { text: T("toPrefix") + " " + (toLine || "–") }), App.icon("chevron-down")]);
    toggle.addEventListener("click", function () { details.hidden = !details.hidden; toggle.setAttribute("aria-expanded", String(!details.hidden)); });
    var star = App.el("button", { type: "button", class: "mail-row__star" + (m.isStarred ? " is-on" : ""), title: T("star") });
    star.innerHTML = '<svg class="icon' + (m.isStarred ? " icon--filled" : "") + '"><use href="#i-star"/></svg>';
    star.addEventListener("click", function () {
      m.isStarred = !m.isStarred;
      star.classList.toggle("is-on", m.isStarred);
      star.querySelector("svg").classList.toggle("icon--filled", m.isStarred);
      App.post("/api/mail/messages/flags", { ids: [m.id], isStarred: m.isStarred });
    });
    var sender = App.el("div", { class: "reader__sender" }, [
      App.avatar(from.name, from.address),
      App.el("div", { class: "reader__who" }, [
        App.el("div", { class: "reader__from" }, [App.el("b", { text: App.displayName(from.name, from.address) }), App.el("span", { class: "muted", text: " <" + from.address + ">" })]),
        toggle
      ]),
      App.el("div", { class: "reader__meta" }, [App.el("span", { class: "reader__date", text: App.formatFullDate(m.date), title: m.date }), options && options.preview ? null : star])
    ]);
    var meta = sender.querySelector(".reader__meta");
    if (m.canSend || m.canEdit) {
      meta.appendChild(App.iconButton("reply", T("reply"), function () { App.compose.reply(m.id, "reply"); }));
      meta.appendChild(App.iconButton("more-vertical", T("more"), function (e) {
        App.showMenu(e.currentTarget, [
          { label: T("reply"), icon: "reply", onClick: function () { App.compose.reply(m.id, "reply"); } },
          { label: T("replyAll"), icon: "reply-all", onClick: function () { App.compose.reply(m.id, "replyall"); } },
          { label: T("forward"), icon: "forward", onClick: function () { App.compose.reply(m.id, "forward"); } }
        ], { alignRight: true });
      }));
    }
    if (onCollapse) { meta.appendChild(App.iconButton("chevron-up", T("collapse"), onCollapse)); }
    wrap.appendChild(sender);
    wrap.appendChild(details);

    if (m.hasRemoteContent) {
      var banner = App.el("div", { class: "reader__banner" }, [App.icon("image"), App.el("span", { text: T("imagesBlocked") })]);
      var show = App.el("button", { type: "button", class: "btn btn--secondary btn--sm", text: T("showImages") });
      show.addEventListener("click", function () { frame.src = m.bodyUrl + "?images=true"; banner.remove(); });
      banner.appendChild(show);
      wrap.appendChild(banner);
    }

    var frame = App.el("iframe", {
      class: "reader__body", sandbox: "allow-same-origin allow-popups allow-popups-to-escape-sandbox", title: T("messageBody"), "data-quote-title": T("showTrimmed"), src: m.bodyUrl
    });
    var viewer = App.viewer.attach(frame);
    var fitButton = App.el("button", { type: "button", class: "btn btn--secondary btn--sm reader__fit", hidden: "hidden", text: T("originalSize") });
    fitButton.addEventListener("click", function () { viewer.setFit(!viewer.fitting); });
    frame.addEventListener("mm-fit", function (e) {
      fitButton.hidden = !e.detail.wide;
      fitButton.textContent = e.detail.fitting ? T("originalSize") : T("fitToWidth");
    });
    wrap.appendChild(fitButton);
    wrap.appendChild(frame);

    if (m.attachments.length) {
      var list = App.el("div", { class: "reader__attachments" }, [App.el("div", { class: "reader__attachments-title", text: T("attachmentsCount").replace("{0}", m.attachments.length) })]);
      var chips = App.el("div", { class: "attachment-list" });
      m.attachments.forEach(function (a) {
        var chip = App.el("a", { class: "attachment", href: a.url, download: a.fileName, title: a.fileName }, [
          App.icon("paperclip"), App.el("span", { class: "attachment__name", text: a.fileName }), App.el("span", { class: "attachment__size", text: App.formatSize(a.size) })
        ]);
        chips.appendChild(chip);
      });
      list.appendChild(chips);
      wrap.appendChild(list);
    }

    if (m.canSend || m.canEdit) {
      wrap.appendChild(App.el("div", { class: "reader__reply" }, [
        replyButton("reply", T("reply"), function () { App.compose.reply(m.id, "reply"); }),
        replyButton("reply-all", T("replyAll"), function () { App.compose.reply(m.id, "replyall"); }),
        replyButton("forward", T("forward"), function () { App.compose.reply(m.id, "forward"); })
      ]));
    }

    return wrap;
  }

  /**
   * Printing needs a page of its own: the reader shows the mail in a sandboxed frame, which the browser may not print (no allow-modals),
   * cannot break into pages and, on phones, does not print at all. The server renders header and body as one document that opens the
   * print dialog itself; it opens in a new window (the same window when the browser refuses a second one).
   */
  function printMessage(m) {
    var url = "/api/mail/messages/" + m.id + "/print";
    if (!window.open(url, "_blank")) { window.location.assign(url); }
  }

  /** The share sheet of phones and tablets (from which a message can be printed, saved or sent on); desktops print directly. */
  function canShare() {
    return typeof navigator.share === "function" && window.matchMedia("(pointer: coarse)").matches;
  }

  /** Shares the message as an .eml file when the device takes files, as text otherwise. */
  function shareMessage(m) {
    var title = m.subject || T("noSubject");
    var text = T("from") + ": " + addrLabel(m.from) + "\n" + title;
    var fallback = function () { return navigator.share({ title: title, text: text }); };
    var name = title.replace(/[\\/:*?"<>|\r\n]+/g, " ").trim().slice(0, 80) || "message";
    fetch("/api/mail/messages/" + m.id + "/raw", { credentials: "same-origin" }).then(function (response) {
      if (!response.ok) { throw new Error("HTTP " + response.status); }
      return response.blob();
    }).then(function (blob) {
      var data = { files: [new File([blob], name + ".eml", { type: "message/rfc822" })], title: title, text: text };
      return navigator.canShare && navigator.canShare(data) ? navigator.share(data) : fallback();
    }).catch(function (e) {
      if (e && e.name === "AbortError") { return; }   // the person closed the sheet
      fallback().catch(function (again) { if (!again || again.name !== "AbortError") { App.toast(again && again.message ? again.message : String(again), { error: true }); } });
    });
  }

  function detailRow(label, value) { return App.el("div", { class: "reader__detail" }, [App.el("span", { class: "reader__detail-label", text: label }), App.el("span", { text: value })]); }

  function replyButton(icon, label, onClick) {
    var button = App.el("button", { type: "button", class: "btn btn--secondary" }, [App.icon(icon), App.el("span", { text: label })]);
    button.addEventListener("click", onClick);
    return button;
  }

  // ---------------------------------------------------------------------------------------------
  // Keyboard
  // ---------------------------------------------------------------------------------------------
  function onKey(e) {
    var target = e.target;
    var typing = target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT" || target.isContentEditable);
    if (typing || e.ctrlKey || e.metaKey || e.altKey || doc.querySelector("dialog[open]")) { return; }
    var key = e.key;
    var open = !!S.messageId;
    var inReader = open && !isSplit();   // next to a reading pane the list keys keep working
    var cursorItem = S.items[S.cursor];

    if (key === "c") { e.preventDefault(); App.compose.open({}); }
    else if (key === "/") { e.preventDefault(); els.searchInput.focus(); }
    else if (key === "j" && !inReader) { moveCursor(1); }
    else if (key === "k" && !inReader) { moveCursor(-1); }
    else if ((key === "Enter" || key === "o") && !inReader && cursorItem) { openItem(cursorItem); }
    else if (key === "u" && (open || S.previewId)) { navigate({ m: 0 }); }
    else if (key === "x" && !inReader && cursorItem) { toggleSelect(cursorItem.id, !S.selected.has(cursorItem.id)); renderList(); }
    else if (key === "s" && !inReader && cursorItem) { act(cursorItem.isStarred ? "unstar" : "star", [cursorItem.id]); }
    else if (key === "e") { shortcutAct("archive"); }
    else if (key === "#" || key === "Delete") { shortcutAct("delete"); }
    else if (key === "I" && !inReader) { shortcutAct("read"); }
    else if (key === "U" && !inReader) { shortcutAct("unread"); }
    else if (open && key === "r") { e.preventDefault(); App.compose.reply(S.messageId, "reply"); }
    else if (open && key === "a") { e.preventDefault(); App.compose.reply(S.messageId, "replyall"); }
    else if (open && key === "f") { e.preventDefault(); App.compose.reply(S.messageId, "forward"); }
  }

  /** A key for the selection: with "everything that matches" selected it covers all of it. */
  function shortcutAct(kind) {
    if (S.allMatching && (!S.messageId || isSplit())) { actSelected(kind); return; }
    var ids = targetIds();
    if (ids.length) { act(kind, ids); }
  }

  function targetIds() {
    if (isSplit() && S.selected.size) { return Array.from(S.selected); }   // the list is in sight: what is ticked there comes first
    if (S.messageId) { return [S.messageId]; }
    if (S.selected.size) { return Array.from(S.selected); }
    var item = S.items[S.cursor];
    return item ? [item.id] : [];
  }

  var openTimer = 0;
  function moveCursor(delta) {
    if (!S.items.length) { return; }
    S.cursor = Math.max(0, Math.min(S.items.length - 1, S.cursor + delta));
    var rows = els.list.querySelectorAll(".mail-row");
    rows.forEach(function (r, i) { r.classList.toggle("is-cursor", i === S.cursor); });
    if (rows[S.cursor]) { rows[S.cursor].scrollIntoView({ block: "nearest" }); }
    // Next to a reading pane the message under the cursor is the one shown there (after a moment, so that holding the key does not load them all).
    if (isSplit()) {
      var item = S.items[S.cursor];
      clearTimeout(openTimer);
      if (item && !item.isDraft) { openTimer = setTimeout(function () { navigate({ m: item.id }); }, 250); }
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Live updates
  // ---------------------------------------------------------------------------------------------
  function connectEvents() {
    if (!window.EventSource) { setInterval(function () { refreshCounts(); }, 60000); return; }
    var pending = null;
    var source = new EventSource("/api/mail/events");
    source.addEventListener("mail", function (e) {
      var evt = {};
      try { evt = JSON.parse(e.data); } catch (err) { return; }
      clearTimeout(pending);
      pending = setTimeout(function () {
        if (evt.kind === "FoldersChanged") { refreshBootstrap(); return; }
        App.get("/api/mail/counts?mailboxId=" + evt.mailboxId).then(App.applyCounts);
        if (evt.mailboxId === S.mailboxId && (!S.messageId || isSplit()) && S.page === 1 && !S.query && (!evt.folderId || evt.folderId === S.folderId || S.folderId === 0)) { loadList(true); }
      }, 600);
    });
  }

  if (doc.readyState === "loading") { doc.addEventListener("DOMContentLoaded", init); } else { init(); }
})();
