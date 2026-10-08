// MatMail web client – the compose windows (docked bottom right; full screen on phones): recipients with suggestions,
// rich text editor, attachments, signatures, draft auto-save, send.
(function () {
  "use strict";

  var App = window.MailApp;
  var doc = document;
  var T = App.T;
  var windows = [];
  var EMAIL = /^[^\s@<>,;]+@[^\s@<>,;]+\.[^\s@<>,;]+$/;

  App.compose = { setup: setup, open: open, openDraft: openDraft, reply: reply, openFromOutbox: openFromOutbox };

  function setup() { /* identities and signatures come with the bootstrap data */ }

  // ---------------------------------------------------------------------------------------------
  // Opening
  // ---------------------------------------------------------------------------------------------
  function open(options) {
    var model = options.model || {};
    if (windows.length >= 3 && !window.matchMedia("(max-width: 720px)").matches) { App.toast(T("tooManyWindows"), { error: true }); return null; }
    var win = createWindow(model, options);
    return win;
  }

  function openDraft(id) {
    return App.get("/api/mail/compose/draft/" + id).then(function (model) { open({ model: model, isDraft: true }); }).catch(function (e) { App.toast(e.message, { error: true }); });
  }

  /** Writes a message again that waits in the outbox of the device (see mail-outbox.js): its files come back as files, the uploaded ones by their ids. */
  function openFromOutbox(item) {
    var model = Object.assign({}, item.model, { attachments: item.uploaded || [] });
    var win = open({ model: model, isDraft: true });
    if (win) {
      if (item.files && item.files.length) { addFiles(win, item.files.map(function (f) { return new File([f.blob], f.name, { type: f.type }); })); }
      win.dirty = true;
    }
    return win;
  }

  function reply(messageId, mode) {
    return App.get("/api/mail/compose/reply?messageId=" + messageId + "&mode=" + mode).then(function (model) { open({ model: model, isReply: true }); }).catch(function (e) { App.toast(e.message, { error: true }); });
  }

  // ---------------------------------------------------------------------------------------------
  // Window
  // ---------------------------------------------------------------------------------------------
  function createWindow(model, options) {
    var identities = App.boot.identities;
    if (!identities.length) { App.toast(T("noIdentity"), { error: true }); return null; }

    var win = { model: model, draftId: model.draftId || null, attachments: [], dirty: false, sending: false, saving: null, timer: null, uploading: 0 };
    var defaultIdentity = identities.filter(function (i) { return i.address === model.from; })[0] || identities.filter(function (i) { return i.isOwn && i.isPrimary; })[0] || identities[0];

    var title = App.el("span", { class: "compose__title", text: model.subject ? model.subject : T("newMessage") });
    var minimize = App.iconButton("minus", T("minimize"), function () { root.classList.toggle("is-minimized"); });
    var maximize = App.iconButton("maximize", T("fullScreen"), function () { root.classList.toggle("is-maximized"); });
    var close = App.iconButton("x", T("saveAndClose"), function () { closeWindow(win, true); });
    var header = App.el("header", { class: "compose__header" }, [title, App.el("span", { class: "compose__header-actions" }, [minimize, maximize, close])]);
    header.addEventListener("click", function (e) { if (e.target === header || e.target === title) { root.classList.toggle("is-minimized"); } });

    var from = App.el("select", { class: "compose__from", "aria-label": T("from") });
    identities.forEach(function (i) { from.appendChild(App.el("option", { value: i.address, text: i.label, selected: i.address === defaultIdentity.address })); });
    var fromRow = App.el("div", { class: "compose__row compose__row--from" }, [App.el("label", { text: T("from") }), from]);
    fromRow.hidden = identities.length < 2;

    var to = chips(win, model.to || [], T("to"));
    var cc = chips(win, model.cc || [], T("cc"));
    var bcc = chips(win, model.bcc || [], T("bcc"));
    var ccToggle = App.el("button", { type: "button", class: "compose__link", text: T("cc") });
    var bccToggle = App.el("button", { type: "button", class: "compose__link", text: T("bcc") });
    var toRow = App.el("div", { class: "compose__row" }, [App.el("label", { text: T("to") }), to.node, App.el("span", { class: "compose__links" }, [ccToggle, bccToggle])]);
    var ccRow = App.el("div", { class: "compose__row", hidden: !(model.cc && model.cc.length) }, [App.el("label", { text: T("cc") }), cc.node]);
    var bccRow = App.el("div", { class: "compose__row", hidden: !(model.bcc && model.bcc.length) }, [App.el("label", { text: T("bcc") }), bcc.node]);
    ccToggle.addEventListener("click", function () { ccRow.hidden = false; ccToggle.hidden = true; cc.input.focus(); });
    bccToggle.addEventListener("click", function () { bccRow.hidden = false; bccToggle.hidden = true; bcc.input.focus(); });
    ccToggle.hidden = !ccRow.hidden;
    bccToggle.hidden = !bccRow.hidden;

    var subject = App.el("input", { class: "compose__subject", type: "text", placeholder: T("subject"), "aria-label": T("subject"), value: model.subject || "" });
    subject.addEventListener("input", function () { title.textContent = subject.value || T("newMessage"); markDirty(win); });
    var subjectRow = App.el("div", { class: "compose__row compose__row--subject" }, [subject]);

    var editor = App.el("div", { class: "compose__editor", contenteditable: "true", role: "textbox", "aria-multiline": "true", "aria-label": T("messageBody") });
    editor.innerHTML = model.html || "<div><br></div>";
    editor.addEventListener("input", function () { markDirty(win); });
    editor.addEventListener("paste", function (e) { pasteImages(win, e); });

    var attachmentsBox = App.el("div", { class: "compose__attachments" });
    var fileInput = App.el("input", { type: "file", multiple: true, hidden: true });
    fileInput.addEventListener("change", function () { addFiles(win, Array.from(fileInput.files)); fileInput.value = ""; });

    var signatureSelect = App.el("select", { class: "compose__signature", "aria-label": T("signature"), title: T("signature") });
    signatureSelect.addEventListener("change", function () { applySignature(win); markDirty(win); });

    var toolbar = App.el("div", { class: "compose__toolbar" }, [
      toolButton("bold", T("bold"), function () { format("bold"); }),
      toolButton("italic", T("italic"), function () { format("italic"); }),
      toolButton("underline", T("underline"), function () { format("underline"); }),
      App.el("span", { class: "mail-toolbar__sep" }),
      toolButton("list", T("bulletList"), function () { format("insertUnorderedList"); }),
      toolButton("list-ordered", T("numberedList"), function () { format("insertOrderedList"); }),
      toolButton("link", T("link"), function () {
        var selection = saveSelection();
        App.promptDialog(T("link"), "https://", "URL").then(function (url) { restoreSelection(selection); if (url) { format("createLink", url); } });
      }),
      toolButton("x", T("clearFormatting"), function () { format("removeFormat"); })
    ]);

    var send = App.el("button", { type: "button", class: "btn btn--primary compose__send" }, [App.el("span", { text: T("send") })]);
    var attach = App.iconButton("paperclip", T("attachFiles"), function () { fileInput.click(); }, "compose__attach");
    var discard = App.iconButton("trash", T("discardDraft"), function () { discardWindow(win); }, "compose__discard");
    var status = App.el("span", { class: "compose__status", role: "status" });
    var footer = App.el("footer", { class: "compose__footer" }, [send, attach, signatureSelect, App.el("span", { class: "compose__spacer" }), status, discard]);

    var body = App.el("div", { class: "compose__body" }, [fromRow, toRow, ccRow, bccRow, subjectRow, toolbar, editor, attachmentsBox, fileInput]);
    var root = App.el("section", { class: "compose", role: "dialog", "aria-label": T("newMessage") }, [header, body, footer]);

    root.addEventListener("dragover", function (e) { if (e.dataTransfer && Array.from(e.dataTransfer.types).indexOf("Files") >= 0) { e.preventDefault(); root.classList.add("is-dropping"); } });
    root.addEventListener("dragleave", function () { root.classList.remove("is-dropping"); });
    root.addEventListener("drop", function (e) { root.classList.remove("is-dropping"); if (e.dataTransfer && e.dataTransfer.files.length) { e.preventDefault(); addFiles(win, Array.from(e.dataTransfer.files)); } });
    root.addEventListener("keydown", function (e) {
      if ((e.ctrlKey || e.metaKey) && e.key === "Enter") { e.preventDefault(); sendWindow(win); }
      if (e.key === "Escape" && root.classList.contains("is-maximized")) { root.classList.remove("is-maximized"); }
    });
    send.addEventListener("click", function () { sendWindow(win); });
    from.addEventListener("change", function () { fillSignatures(win); applySignature(win); markDirty(win); });

    win.root = root; win.title = title; win.from = from; win.to = to; win.cc = cc; win.bcc = bcc; win.subject = subject; win.editor = editor;
    win.attachmentsBox = attachmentsBox; win.signature = signatureSelect; win.status = status; win.send = send; win.footerSend = send;

    doc.getElementById("compose-dock").appendChild(root);
    windows.push(win);

    fillSignatures(win, model.signatureId);
    if (!options.isDraft) { applySignature(win, true); }

    // Attachments that come with a draft or a forward.
    (model.attachments || []).forEach(function (a) { win.attachments.push({ id: a.id, name: a.fileName, size: a.size, done: true }); });
    renderAttachments(win);

    win.timer = setInterval(function () { autosave(win); }, 20000);
    setTimeout(function () { (model.to && model.to.length ? win.editor : win.to.input).focus(); if (model.to && model.to.length) { placeCaretAtStart(win.editor); } }, 50);
    if (options.isReply) { win.dirty = false; }
    return win;
  }

  function toolButton(icon, label, onClick) {
    var button = App.iconButton(icon, label, null, "compose__tool");
    button.addEventListener("mousedown", function (e) { e.preventDefault(); });
    button.addEventListener("click", onClick);
    return button;
  }

  function format(command, value) { doc.execCommand(command, false, value || null); }

  function saveSelection() {
    var sel = window.getSelection();
    return sel.rangeCount ? sel.getRangeAt(0).cloneRange() : null;
  }

  function restoreSelection(range) {
    if (!range) { return; }
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
  }

  function placeCaretAtStart(node) {
    var range = doc.createRange();
    range.setStart(node, 0);
    range.collapse(true);
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
  }

  // ---------------------------------------------------------------------------------------------
  // Recipients (chips with suggestions)
  // ---------------------------------------------------------------------------------------------
  function chips(win, initial, label) {
    var values = [];
    var input = App.el("input", { type: "text", class: "chips__input", "aria-label": label, autocomplete: "off", spellcheck: "false" });
    var node = App.el("div", { class: "chips" }, [input]);
    var list = App.el("div", { class: "suggest", hidden: true, role: "listbox" });
    node.appendChild(list);
    var suggestions = [], active = -1;
    var api = { node: node, input: input, values: function () { commit(); return values.slice(); } };

    function render() {
      Array.from(node.querySelectorAll(".chip-item")).forEach(function (c) { c.remove(); });
      values.forEach(function (value, index) {
        var parsed = parse(value);
        var chip = App.el("span", { class: "chip-item" + (EMAIL.test(parsed.address) ? "" : " is-invalid"), title: parsed.address }, [
          App.el("span", { text: parsed.name || parsed.address })
        ]);
        var remove = App.el("button", { type: "button", class: "chip-item__remove", "aria-label": "×", text: "×" });
        remove.addEventListener("click", function () { values.splice(index, 1); render(); markDirty(win); });
        chip.appendChild(remove);
        node.insertBefore(chip, input);
      });
    }

    function add(value) {
      var text = value.trim().replace(/[;,]+$/, "");
      if (!text) { return; }
      if (!values.some(function (v) { return parse(v).address.toLowerCase() === parse(text).address.toLowerCase(); })) { values.push(text); }
      render(); markDirty(win);
    }

    function commit() {
      var text = input.value;
      if (text.trim()) { text.split(/[;,\n]+/).forEach(add); input.value = ""; }
      hide();
    }

    function hide() { list.hidden = true; active = -1; }

    function show() {
      list.innerHTML = "";
      suggestions.forEach(function (s, index) {
        var item = App.el("div", { class: "suggest__item" + (index === active ? " is-active" : ""), role: "option" }, [
          App.avatar(s.name, s.address),
          App.el("span", { class: "suggest__text" }, [App.el("b", { text: s.name || s.address }), s.name ? App.el("span", { class: "muted", text: " " + s.address }) : null])
        ]);
        item.addEventListener("mousedown", function (e) { e.preventDefault(); pick(s); });
        list.appendChild(item);
      });
      list.hidden = !suggestions.length;
    }

    function pick(s) { input.value = ""; add(s.name ? s.name + " <" + s.address + ">" : s.address); hide(); input.focus(); }

    var lookup = App.debounce(function () {
      var q = input.value.trim();
      if (q.length < 1) { suggestions = []; show(); return; }
      App.get("/api/mail/contacts?q=" + encodeURIComponent(q)).then(function (result) { suggestions = result || []; active = suggestions.length ? 0 : -1; show(); }).catch(function () { hide(); });
    }, 180);

    input.addEventListener("input", function () {
      if (/[;,]\s*$/.test(input.value)) { commit(); } else { lookup(); }
    });
    input.addEventListener("keydown", function (e) {
      if (e.key === "ArrowDown" && suggestions.length) { e.preventDefault(); active = (active + 1) % suggestions.length; show(); }
      else if (e.key === "ArrowUp" && suggestions.length) { e.preventDefault(); active = (active - 1 + suggestions.length) % suggestions.length; show(); }
      else if ((e.key === "Enter" || e.key === "Tab") && !list.hidden && active >= 0) { e.preventDefault(); pick(suggestions[active]); }
      else if (e.key === "Enter" || (e.key === "Tab" && input.value.trim())) { if (input.value.trim()) { e.preventDefault(); commit(); } }
      else if (e.key === "Backspace" && !input.value && values.length) { values.pop(); render(); markDirty(win); }
      else if (e.key === "Escape") { hide(); }
    });
    input.addEventListener("blur", function () { setTimeout(commit, 120); });
    input.addEventListener("paste", function (e) {
      var pasted = (e.clipboardData || window.clipboardData).getData("text");
      if (/[;,\n]/.test(pasted)) { e.preventDefault(); pasted.split(/[;,\n]+/).forEach(add); }
    });
    node.addEventListener("click", function (e) { if (e.target === node) { input.focus(); } });

    (initial || []).forEach(function (v) { values.push(v); });
    render();
    return api;
  }

  function parse(value) {
    var match = /^\s*(?:"?([^"<]*)"?\s*)?<([^>]+)>\s*$/.exec(value);
    return match ? { name: (match[1] || "").trim(), address: match[2].trim() } : { name: "", address: value.trim() };
  }

  // ---------------------------------------------------------------------------------------------
  // Signature
  // ---------------------------------------------------------------------------------------------
  function identityOf(win) {
    return App.boot.identities.filter(function (i) { return i.address === win.from.value; })[0];
  }

  function signaturesFor(win) {
    var identity = identityOf(win);
    return App.boot.signatures.filter(function (s) { return s.scope !== "Mailbox" || s.mailboxId === (identity && identity.mailboxId); });
  }

  function fillSignatures(win, selectedId) {
    var list = signaturesFor(win);
    win.signature.innerHTML = "";
    win.signature.appendChild(App.el("option", { value: "", text: T("noSignature") }));
    list.forEach(function (s) { win.signature.appendChild(App.el("option", { value: s.id, text: s.name })); });
    var wanted = selectedId || (list.filter(function (s) { return s.isDefault; })[0] || {}).id || "";
    win.signature.value = String(wanted);
    win.signature.hidden = list.length === 0;
  }

  function applySignature(win, initial) {
    var existing = win.editor.querySelector(".mm-signature");
    if (existing) { existing.remove(); }
    var chosen = App.boot.signatures.filter(function (s) { return String(s.id) === win.signature.value; })[0];
    if (!chosen) { return; }
    var block = doc.createElement("div");
    block.className = "mm-signature";
    block.setAttribute("data-signature-id", chosen.id);
    block.innerHTML = "-- <br>" + chosen.html;
    var anchor = win.editor.querySelector(".mm-quote-header, .mm-forward-header");
    if (anchor) { win.editor.insertBefore(block, anchor); } else { win.editor.appendChild(block); }
    if (!initial) { markDirty(win); }
  }

  // ---------------------------------------------------------------------------------------------
  // Attachments
  // ---------------------------------------------------------------------------------------------
  function addFiles(win, files) {
    files.forEach(function (file) {
      if (file.size > App.boot.settings.maxUploadMb * 1024 * 1024) { App.toast(T("fileTooLarge").replace("{0}", file.name).replace("{1}", App.boot.settings.maxUploadMb), { error: true }); return; }
      var entry = { id: null, name: file.name, size: file.size, done: false, progress: 0, file: file, pending: false };
      win.attachments.push(entry);
      if (App.outbox.isOffline()) { keepForLater(entry); renderAttachments(win); markDirty(win); return; }
      win.uploading++;
      renderAttachments(win);
      uploadEntry(win, entry).then(function () { markDirty(win); }, function (error) {
        // No connection (or it went): the file is kept and goes up with the message. Anything else is the server saying no.
        if (App.outbox.isNetworkError(error)) { keepForLater(entry); markDirty(win); }
        else { win.attachments.splice(win.attachments.indexOf(entry), 1); App.toast(file.name + ": " + error.message, { error: true }); }
      }).then(function () { win.uploading--; renderAttachments(win); });
    });
  }

  /** A file that is not on the server yet but stays with the message. */
  function keepForLater(entry) { entry.pending = true; entry.done = true; }

  function pendingEntries(win) { return win.attachments.filter(function (a) { return a.pending && a.file; }); }

  /** Rejects with an error that has a status when the server refused, and without one when the connection is gone. */
  function uploadEntry(win, entry) {
    return new Promise(function (resolve, reject) {
      var form = new FormData();
      form.append("file", entry.file, entry.name);
      var xhr = new XMLHttpRequest();
      xhr.open("POST", "/api/mail/attachments");
      xhr.setRequestHeader("X-CSRF-TOKEN", doc.querySelector('meta[name="csrf-token"]').getAttribute("content"));
      xhr.upload.onprogress = function (e) { if (e.lengthComputable) { entry.progress = e.loaded / e.total; renderAttachments(win); } };
      xhr.onload = function () {
        if (xhr.status === 200) {
          var data = JSON.parse(xhr.responseText);
          entry.id = data.id; entry.done = true; entry.pending = false; entry.size = data.size; entry.file = null;
          resolve();
        } else {
          var message = T("uploadFailed");
          try { message = JSON.parse(xhr.responseText).error || message; } catch (e) { /* default */ }
          var error = new Error(message);
          error.status = xhr.status;
          reject(error);
        }
      };
      xhr.onerror = function () { reject(new Error(T("uploadFailed"))); };
      xhr.send(form);
    });
  }

  /** Uploads what was kept for later, one file after the other; on a failure the files that are left stay kept. */
  function uploadPending(win) {
    return pendingEntries(win).reduce(function (chain, entry) {
      return chain.then(function () {
        entry.pending = false; entry.done = false; entry.progress = 0;
        renderAttachments(win);
        return uploadEntry(win, entry);
      }).catch(function (error) { keepForLater(entry); renderAttachments(win); throw error; });
    }, Promise.resolve());
  }

  function renderAttachments(win) {
    win.attachmentsBox.innerHTML = "";
    win.attachments.forEach(function (entry) {
      var chip = App.el("span", { class: "attachment" + (entry.done ? "" : " is-uploading") + (entry.pending ? " is-pending" : ""), title: entry.pending ? T("attachmentWaiting") : null }, [
        App.icon("paperclip"),
        App.el("span", { class: "attachment__name", text: entry.name }),
        App.el("span", { class: "attachment__size", text: entry.done ? App.formatSize(entry.size) : Math.round((entry.progress || 0) * 100) + " %" })
      ]);
      var remove = App.iconButton("x", T("remove"), function () {
        win.attachments.splice(win.attachments.indexOf(entry), 1);
        if (entry.id) { App.api("DELETE", "/api/mail/attachments/" + entry.id).catch(function () { /* cleaned up later */ }); }
        renderAttachments(win); markDirty(win);
      }, "attachment__remove");
      chip.appendChild(remove);
      win.attachmentsBox.appendChild(chip);
    });
    win.send.disabled = win.uploading > 0 || win.sending;
  }

  function pasteImages(win, e) {
    var items = (e.clipboardData && e.clipboardData.items) || [];
    var images = Array.from(items).filter(function (i) { return i.kind === "file" && i.type.indexOf("image/") === 0; });
    if (!images.length) { return; }
    e.preventDefault();
    images.forEach(function (item) {
      var reader = new FileReader();
      reader.onload = function () { doc.execCommand("insertImage", false, reader.result); markDirty(win); };
      reader.readAsDataURL(item.getAsFile());
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Model, drafts, sending
  // ---------------------------------------------------------------------------------------------
  function markDirty(win) { win.dirty = true; win.status.textContent = ""; }

  function hasContent(win) {
    if (win.to.values().length || win.cc.values().length || win.bcc.values().length) { return true; }
    if (win.subject.value.trim() || win.attachments.length) { return true; }
    // The body counts when there is more than the signature and the quoted original.
    var clone = win.editor.cloneNode(true);
    clone.querySelectorAll(".mm-signature, .mm-quote, .mm-quote-header, .mm-forward-header").forEach(function (n) { n.remove(); });
    return clone.innerText.trim().length > 0 || !!clone.querySelector("img");
  }

  function buildModel(win) {
    return {
      draftId: win.draftId,
      from: win.from.value,
      to: win.to.values(), cc: win.cc.values(), bcc: win.bcc.values(),
      subject: win.subject.value,
      html: win.editor.innerHTML,
      inReplyTo: win.model.inReplyTo || null,
      references: win.model.references || null,
      replyToMessageId: win.model.replyToMessageId || null,
      forwardOfMessageId: win.model.forwardOfMessageId || null,
      includeOriginalAttachments: !!win.model.includeOriginalAttachments,
      attachmentIds: win.attachments.filter(function (a) { return a.id; }).map(function (a) { return a.id; })
    };
  }

  function autosave(win) {
    if (!win.dirty || win.sending || win.uploading || win.saving) { return Promise.resolve(); }
    if (!hasContent(win)) { return Promise.resolve(); }
    win.dirty = false;
    win.status.textContent = T("saving");
    win.saving = App.post("/api/mail/drafts", buildModel(win)).then(function (result) {
      win.draftId = result.draftId;
      win.status.textContent = T("draftSaved");
      if (App.handlers.reloadList && App.state.query === "" && App.folder(App.state.folderId) && App.folder(App.state.folderId).folder.kind === "Drafts") { App.handlers.reloadList(); }
    }).catch(function (e) { win.dirty = true; win.status.textContent = App.outbox.isNetworkError(e) ? T("offlineDraftStatus") : e.message; }).then(function () { win.saving = null; });
    return win.saving;
  }

  function sendWindow(win) {
    if (win.sending || win.uploading) { return; }
    var recipients = win.to.values().concat(win.cc.values(), win.bcc.values());
    if (!recipients.length) { win.status.textContent = T("addRecipient"); win.to.input.focus(); return; }
    var invalid = recipients.filter(function (r) { return !EMAIL.test(parse(r).address); });
    if (invalid.length) { win.status.textContent = T("invalidRecipient").replace("{0}", invalid[0]); return; }

    var proceed = function () {
      win.sending = true;
      win.send.disabled = true;
      win.status.textContent = T("sending");
      win.status.classList.remove("is-error");
      Promise.resolve(win.saving).then(function () {
        if (App.outbox.isOffline()) { throw new Error("offline"); }   // no status: the connection is gone
        return uploadPending(win);
      }).then(function () {
        return App.post("/api/mail/send", buildModel(win));
      }).then(function () {
        closeWindow(win, false);
        App.toast(T("messageSent"));
        if (App.handlers.reloadList) { App.handlers.reloadList(); }
      }).catch(function (e) {
        if (App.outbox.isNetworkError(e)) { return queueWindow(win, "send"); }   // kept on the device, sent as soon as the connection is back
        win.sending = false; win.send.disabled = false;
        win.status.textContent = e.message;
        win.status.classList.add("is-error");
      });
    };

    if (!win.subject.value.trim()) { App.confirm(T("sendWithoutSubject")).then(function (ok) { if (ok) { proceed(); } }); } else { proceed(); }
  }

  /** Keeps the message on the device (the outbox) instead of on the server: it is sent, or saved as a draft, when the connection is back. */
  function queueWindow(win, kind) {
    var files = pendingEntries(win).map(function (a) { return { name: a.name, type: a.file.type, blob: a.file }; });
    var uploaded = win.attachments.filter(function (a) { return a.id; }).map(function (a) { return { id: a.id, fileName: a.name, size: a.size }; });
    return App.outbox.add(kind, buildModel(win), files, uploaded).then(function () {
      closeWindow(win, false);
      App.toast(kind === "send" ? T("queuedToSend") : T("savedOnDevice"));
    }, function () {
      win.sending = false; win.send.disabled = false;
      win.status.textContent = T("queueFailed");
      win.status.classList.add("is-error");
    });
  }

  function closeWindow(win, saveFirst) {
    var finish = function () { clearInterval(win.timer); win.root.remove(); windows.splice(windows.indexOf(win), 1); };
    if (saveFirst && win.dirty && hasContent(win) && !win.sending) {
      autosave(win).then(function () {
        if (win.dirty) { return queueWindow(win, "draft"); }   // the server could not be reached: the draft stays on this device
        finish(); App.toast(T("draftSaved")); if (App.handlers.reloadList) { App.handlers.reloadList(); }
      });
    } else {
      Promise.resolve(win.saving).then(finish);
    }
  }

  function discardWindow(win) {
    var run = function () {
      win.attachments.forEach(function (a) { if (a.id) { App.api("DELETE", "/api/mail/attachments/" + a.id).catch(function () { /* later */ }); } });
      var after = function () { clearInterval(win.timer); win.root.remove(); windows.splice(windows.indexOf(win), 1); if (App.handlers.reloadList) { App.handlers.reloadList(); } };
      Promise.resolve(win.saving).then(function () {
        if (win.draftId) { return App.api("DELETE", "/api/mail/drafts/" + win.draftId).catch(function () { /* gone already */ }); }
      }).then(after);
    };
    if (hasContent(win)) { App.confirm(T("confirmDiscard")).then(function (ok) { if (ok) { run(); } }); } else { run(); }
  }

  window.addEventListener("beforeunload", function (e) {
    var unsaved = windows.some(function (w) { return w.dirty && hasContent(w) && !w.sending; });
    if (unsaved) { e.preventDefault(); e.returnValue = ""; }
  });
})();
