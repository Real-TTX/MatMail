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
    els.search = doc.getElementById("mail-search");
    els.searchInput = doc.getElementById("mail-search-input");
    els.searchClear = doc.getElementById("mail-search-clear");

    App.handlers.renderFolders = renderFolders;
    App.handlers.reloadList = function () { return loadList(true); };

    doc.getElementById("mail-compose-button").addEventListener("click", function () { App.compose.open({}); });
    doc.getElementById("mail-compose-fab").addEventListener("click", function () { App.compose.open({}); });
    els.search.addEventListener("submit", function (e) { e.preventDefault(); search(els.searchInput.value.trim()); });
    els.searchClear.addEventListener("click", function () { els.searchInput.value = ""; navigate({ q: "", page: 1, m: 0 }); els.searchInput.focus(); });
    els.searchInput.addEventListener("input", function () { els.searchClear.hidden = !els.searchInput.value; });
    App.search.attach({ wrap: els.search, input: els.searchInput, options: doc.getElementById("mail-search-options"), run: search });
    window.addEventListener("hashchange", route);
    doc.addEventListener("keydown", onKey);

    showLoading(true);
    App.get("/api/mail/bootstrap").then(function (boot) {
      App.boot = boot;
      App.compose.setup();
      renderFolders();
      route();
      connectEvents();
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
      m: parseInt(params.get("m"), 10) || 0
    };
  }

  function navigate(changes) {
    var current = { mailbox: S.mailboxId, folder: S.folderId, page: S.page, q: S.query, m: S.messageId };
    Object.keys(changes).forEach(function (key) { current[key] = changes[key]; });
    var params = new URLSearchParams();
    if (current.mailbox) { params.set("mailbox", current.mailbox); }
    if (current.folder) { params.set("folder", current.folder); }
    if (current.page > 1) { params.set("page", current.page); }
    if (current.q) { params.set("q", current.q); }
    if (current.m) { params.set("m", current.m); }
    var hash = "#" + params.toString();
    if (location.hash === hash) { route(); } else { location.hash = hash; }
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
    S.messageId = h.m;
    els.searchInput.value = h.q;
    els.searchClear.hidden = !h.q;
    renderFolders();
    if (window.MatMail && window.MatMail.setSidebarOpen) { window.MatMail.setSidebarOpen(false); }

    if (h.m) {
      showReader();
      if (previousMessage !== h.m) { loadMessage(h.m); }
      if (!sameList && !S.items.length) { loadList(true); }
    } else {
      showList();
      if (!sameList || previousMessage) { loadList(false); }
    }
  }

  function showList() { els.listPane.hidden = false; els.readerPane.hidden = true; els.app.classList.remove("is-reading"); doc.title = pageTitle(); }
  function showReader() { els.listPane.hidden = true; els.readerPane.hidden = false; els.app.classList.add("is-reading"); }
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
    App.boot.mailboxes.forEach(function (box, index) {
      var group = App.el("div", { class: "folder-group" });
      var collapsedKey = "matmail-collapsed-" + box.id;
      var hasSelection = box.id === S.mailboxId;
      var collapsed = !box.isOwn && !hasSelection && localStorage.getItem(collapsedKey) !== "0";

      if (!box.isOwn || App.boot.mailboxes.length > 1) {
        var heading = App.el("button", { type: "button", class: "folder-group__title", "aria-expanded": String(!collapsed) }, [
          App.icon(collapsed ? "chevron-right" : "chevron-down"),
          App.icon(box.type === "Shared" ? "users" : box.type === "Unassigned" ? "alert-circle" : "user"),
          App.el("span", { class: "folder-group__name", text: box.isOwn ? T("myMailbox") : box.name })
        ]);
        heading.addEventListener("click", function () { localStorage.setItem(collapsedKey, collapsed ? "0" : "1"); renderFolders(); });
        group.appendChild(heading);
      }

      if (!collapsed) {
        box.folders.forEach(function (folder) { group.appendChild(folderItem(box, folder)); });
        if (box.canManage && box.type !== "Unassigned") {
          var add = App.el("button", { type: "button", class: "folder-item folder-item--add" }, [App.icon("plus"), App.el("span", { text: T("newFolder") })]);
          add.addEventListener("click", function () { createFolder(box); });
          group.appendChild(add);
        }
      }

      els.folders.appendChild(group);
    });
    doc.title = pageTitle();
  }

  function folderItem(box, folder) {
    var active = box.id === S.mailboxId && folder.id === S.folderId && !S.query;
    var count = folder.kind === "Drafts" ? folder.total : folder.unread;
    var labelText = App.folderLabel(folder);
    var node = App.el("a", {
      class: "folder-item" + (active ? " is-active" : "") + (count && folder.kind !== "Drafts" ? " has-unread" : ""),
      href: "#mailbox=" + box.id + "&folder=" + folder.id,
      title: labelText
    }, [
      App.icon(KIND_ICONS[folder.kind] || "folder"),
      App.el("span", { class: "folder-item__name", text: labelText, style: folder.depth ? "padding-left:" + (folder.depth * 12) + "px" : null }),
      count ? App.el("span", { class: "folder-item__count", text: String(count) }) : null
    ]);
    var more = App.iconButton("more-vertical", T("more"), function (e) { e.preventDefault(); e.stopPropagation(); folderMenu(more, box, folder); }, "folder-item__more");
    node.appendChild(more);
    node.addEventListener("contextmenu", function (e) { e.preventDefault(); folderMenu(node, box, folder); });
    return node;
  }

  function folderMenu(anchor, box, folder) {
    var items = [];
    if (box.canEdit) { items.push({ label: T("markAllRead"), icon: "mail-open", onClick: function () { markFolderRead(folder); } }); }
    if (box.canEdit && (folder.kind === "Trash" || folder.kind === "Junk")) {
      items.push({ label: folder.kind === "Trash" ? T("emptyTrash") : T("emptySpam"), icon: "trash", danger: true, onClick: function () { emptyFolder(folder); } });
    }
    if (box.canManage && folder.kind === "Custom") {
      items.push({ divider: true });
      items.push({ label: T("renameFolder"), icon: "edit", onClick: function () { renameFolder(folder); } });
      items.push({ label: T("deleteFolder"), icon: "trash", danger: true, onClick: function () { deleteFolder(folder); } });
    }
    if (items.length) { App.showMenu(anchor, items, { title: App.folderLabel(folder) }); }
  }

  function createFolder(box) {
    App.promptDialog(T("newFolder"), "", T("folderName")).then(function (name) {
      if (!name) { return; }
      App.post("/api/mail/folders", { mailboxId: box.id, path: name }).then(refreshBootstrap).catch(function (e) { App.toast(e.message, { error: true }); });
    });
  }

  function renameFolder(folder) {
    App.promptDialog(T("renameFolder"), folder.path, T("folderName")).then(function (name) {
      if (!name || name === folder.path) { return; }
      App.api("PATCH", "/api/mail/folders/" + folder.id, { path: name }).then(refreshBootstrap).catch(function (e) { App.toast(e.message, { error: true }); });
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
    var url = "/api/mail/messages?mailboxId=" + S.mailboxId + (S.folderId ? "&folderId=" + S.folderId : "") + "&page=" + S.page + (S.query ? "&q=" + encodeURIComponent(S.query) : "");
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
      showNotice(T("loadFailed") + " " + error.message, true);
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
      var row = App.el("div", { class: "mail-row" + (item.isRead ? "" : " is-unread") + (S.selected.has(item.id) ? " is-selected" : "") + (index === S.cursor ? " is-cursor" : ""), role: "listitem", "data-id": item.id, tabindex: "-1" });

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
      row.appendChild(App.el("div", { class: "mail-row__from", text: who }));

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
    App.post("/api/mail/messages/flags", { ids: [item.id], isStarred: item.isStarred }).catch(function (e) { App.toast(e.message, { error: true }); loadList(true); });
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
  function moveMenu(anchor, ids, everything) {
    var box = currentBox();
    var items = [];
    var run = function (target) { if (everything) { actSelected("move", target); } else { act("move", ids, target); } };
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

  function send(request, message, undo, removes, origin) {
    request.then(function (result) {
      App.applyCounts(result.counts);
      if (removes) {
        S.selected.clear();
        S.allMatching = false;
        if (S.messageId) { navigate({ m: 0 }); } else { loadList(true); }
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
      if (set.has(item.id)) {
        if (changes.isRead !== undefined && item.isRead !== changes.isRead) { wasUnread += changes.isRead ? -1 : 1; }
        Object.keys(changes).forEach(function (k) { item[k] = changes[k]; });
      }
    });
    var folder = currentFolder();
    if (folder && wasUnread) { folder.unread = Math.max(0, folder.unread + wasUnread); renderFolders(); }
  }

  function refreshCounts() {
    App.boot.mailboxes.forEach(function (box) {
      App.get("/api/mail/counts?mailboxId=" + box.id).then(App.applyCounts).catch(function () { /* the next refresh will do */ });
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Reader
  // ---------------------------------------------------------------------------------------------
  var messageRequest = 0;
  function loadMessage(id) {
    var request = ++messageRequest;
    els.reader.innerHTML = "";
    els.readerToolbar.innerHTML = "";
    showLoading(true);
    App.get("/api/mail/messages/" + id).then(function (message) {
      if (request !== messageRequest) { return; }
      showLoading(false);
      S.current = message;
      if (message.isDraft) { App.compose.openDraft(id); navigate({ m: 0 }); return; }
      renderReader(message);
      if (!message.isRead && message.canEdit) {
        App.post("/api/mail/messages/flags", { ids: [id], isRead: true }).then(function (result) { App.applyCounts(result.counts); message.isRead = true; patch([id], { isRead: true }); }).catch(function () { /* stays unread */ });
      }
    }).catch(function (error) {
      if (request !== messageRequest) { return; }
      showLoading(false);
      els.reader.appendChild(App.el("div", { class: "mail-notice is-error", text: error.status === 404 ? T("messageGone") : error.message }));
      els.readerToolbar.appendChild(App.iconButton("arrow-left", T("back"), function () { navigate({ m: 0 }); }));
    });
  }

  function addrLabel(a) { return a ? (a.name ? a.name + " <" + a.address + ">" : a.address) : ""; }

  function renderReader(m) {
    var bar = els.readerToolbar;
    bar.innerHTML = "";
    var box = App.mailbox(m.mailboxId);
    bar.appendChild(App.iconButton("arrow-left", T("back"), function () { navigate({ m: 0 }); }));
    var id = [m.id];
    if (m.canEdit) {
      if (m.folderKind !== "Archive" && m.folderKind !== "Trash") { bar.appendChild(App.iconButton("archive", T("archive"), function () { act("archive", id); })); }
      if (m.folderKind === "Junk") { bar.appendChild(App.iconButton("inbox", T("notSpam"), function () { act("notspam", id); })); }
      else if (m.folderKind !== "Trash") { bar.appendChild(App.iconButton("spam", T("reportSpam"), function () { act("spam", id); })); }
      bar.appendChild(App.iconButton("trash", m.folderKind === "Trash" ? T("deleteForever") : T("delete"), function () { act("delete", id); }));
      bar.appendChild(App.el("span", { class: "mail-toolbar__sep" }));
      bar.appendChild(App.iconButton("mail", T("markUnread"), function () { App.post("/api/mail/messages/flags", { ids: id, isRead: false }).then(function (r) { App.applyCounts(r.counts); navigate({ m: 0 }); }); }));
      bar.appendChild(App.iconButton("folder-input", T("moveTo"), function (e) { moveMenu(e.currentTarget, id); }));
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

    var wrap = App.el("div", { class: "reader" });

    var subjectRow = App.el("div", { class: "reader__head" }, [App.el("h1", { class: "reader__subject", text: m.subject || T("noSubject") })]);
    var hit = App.folder(m.folderId);
    if (hit) { subjectRow.appendChild(App.el("span", { class: "chip", text: App.folderLabel(hit.folder) })); }
    wrap.appendChild(subjectRow);

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
      App.el("div", { class: "reader__meta" }, [App.el("span", { class: "reader__date", text: App.formatFullDate(m.date), title: m.date }), star])
    ]);
    if (m.canSend || m.canEdit) {
      var meta = sender.querySelector(".reader__meta");
      meta.appendChild(App.iconButton("reply", T("reply"), function () { App.compose.reply(m.id, "reply"); }));
      meta.appendChild(App.iconButton("more-vertical", T("more"), function (e) {
        App.showMenu(e.currentTarget, [
          { label: T("reply"), icon: "reply", onClick: function () { App.compose.reply(m.id, "reply"); } },
          { label: T("replyAll"), icon: "reply-all", onClick: function () { App.compose.reply(m.id, "replyall"); } },
          { label: T("forward"), icon: "forward", onClick: function () { App.compose.reply(m.id, "forward"); } }
        ], { alignRight: true });
      }));
    }
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

    els.reader.innerHTML = "";
    els.reader.appendChild(wrap);
    els.reader.scrollTop = 0;
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
    var inReader = !!S.messageId;
    var cursorItem = S.items[S.cursor];

    if (key === "c") { e.preventDefault(); App.compose.open({}); }
    else if (key === "/") { e.preventDefault(); els.searchInput.focus(); }
    else if (key === "j" && !inReader) { moveCursor(1); }
    else if (key === "k" && !inReader) { moveCursor(-1); }
    else if ((key === "Enter" || key === "o") && !inReader && cursorItem) { openItem(cursorItem); }
    else if (key === "u" && inReader) { navigate({ m: 0 }); }
    else if (key === "x" && !inReader && cursorItem) { toggleSelect(cursorItem.id, !S.selected.has(cursorItem.id)); renderList(); }
    else if (key === "s" && !inReader && cursorItem) { act(cursorItem.isStarred ? "unstar" : "star", [cursorItem.id]); }
    else if (key === "e") { shortcutAct("archive"); }
    else if (key === "#" || key === "Delete") { shortcutAct("delete"); }
    else if (key === "I" && !inReader) { shortcutAct("read"); }
    else if (key === "U" && !inReader) { shortcutAct("unread"); }
    else if (inReader && key === "r") { e.preventDefault(); App.compose.reply(S.messageId, "reply"); }
    else if (inReader && key === "a") { e.preventDefault(); App.compose.reply(S.messageId, "replyall"); }
    else if (inReader && key === "f") { e.preventDefault(); App.compose.reply(S.messageId, "forward"); }
  }

  /** A key for the selection: with "everything that matches" selected it covers all of it. */
  function shortcutAct(kind) {
    if (S.allMatching && !S.messageId) { actSelected(kind); return; }
    var ids = targetIds();
    if (ids.length) { act(kind, ids); }
  }

  function targetIds() {
    if (S.messageId) { return [S.messageId]; }
    if (S.selected.size) { return Array.from(S.selected); }
    var item = S.items[S.cursor];
    return item ? [item.id] : [];
  }

  function moveCursor(delta) {
    if (!S.items.length) { return; }
    S.cursor = Math.max(0, Math.min(S.items.length - 1, S.cursor + delta));
    var rows = els.list.querySelectorAll(".mail-row");
    rows.forEach(function (r, i) { r.classList.toggle("is-cursor", i === S.cursor); });
    if (rows[S.cursor]) { rows[S.cursor].scrollIntoView({ block: "nearest" }); }
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
        if (evt.mailboxId === S.mailboxId && !S.messageId && S.page === 1 && !S.query && (!evt.folderId || evt.folderId === S.folderId || S.folderId === 0)) { loadList(true); }
      }, 600);
    });
  }

  if (doc.readyState === "loading") { doc.addEventListener("DOMContentLoaded", init); } else { init(); }
})();
