// MatMail web client – advanced search: the panel under the search box. It writes the query text (from:, to:, subject:, in:, …)
// and reads it back when it is opened again, so whatever the panel builds can also be typed in the box. The server understands
// the text (Messaging/MailQuery.cs); the panel writes the English operators, which work in every language.
(function () {
  "use strict";

  var App = window.MailApp;
  var doc = document;
  var T = App.T;
  var S = App.state;

  var WITHIN = [["", "anyTime"], ["1d", "oneDay"], ["3d", "threeDays"], ["1w", "oneWeek"], ["2w", "twoWeeks"], ["1m", "oneMonth"], ["2m", "twoMonths"], ["6m", "sixMonths"], ["1y", "oneYear"]];
  var FOLDER_WORDS = { Inbox: "inbox", Drafts: "drafts", Sent: "sent", Archive: "archive", Junk: "spam", Trash: "trash" };
  var SYSTEM_WORDS = ["inbox", "sent", "drafts", "archive", "spam", "trash", "anywhere"];
  // What the server accepts besides the English operators.
  var KEYS = {
    from: "from", von: "from", to: "to", an: "to", subject: "subject", betreff: "subject", has: "has", hat: "has", is: "is", ist: "is", "in": "in", ordner: "in",
    before: "before", vor: "before", after: "after", nach: "after", newer_than: "newer_than", neuer_als: "newer_than",
    larger: "larger", "größer": "larger", groesser: "larger", "grösser": "larger", smaller: "smaller", kleiner: "smaller"
  };
  var FOLDER_ALIASES = {
    posteingang: "inbox", gesendet: "sent", draft: "drafts", "entwürfe": "drafts", entwuerfe: "drafts", entwurf: "drafts", archiv: "archive",
    bin: "trash", papierkorb: "trash", junk: "spam", all: "anywhere", "überall": "anywhere", ueberall: "anywhere"
  };
  var ATTACHMENT_WORDS = ["attachment", "attachments", "attach", "anhang", "anhänge", "anhaenge"];

  var ctx = null;        // { wrap, input, options, run } of the search box
  var layer = null;
  var panel = null;
  var fields = {};
  var isOpen = false;
  var counter = 0;

  // ---------------------------------------------------------------------------------------------
  // The text: split into pieces, built from the fields, read back into the fields
  // ---------------------------------------------------------------------------------------------

  /** Pieces separated by blanks; a quoted phrase or a bracket belongs to its piece (so from:"a b" and from:(a OR b) stay whole). */
  function tokenize(text) {
    var tokens = [], i = 0, n = text.length;
    while (i < n) {
      while (i < n && /\s/.test(text.charAt(i))) { i++; }
      if (i >= n) { break; }
      var start = i, depth = 0;
      while (i < n && (depth > 0 || !/\s/.test(text.charAt(i)))) {
        var c = text.charAt(i);
        if (c === '"') { i++; while (i < n && text.charAt(i) !== '"') { i++; } }
        else if (c === "(" || c === "{") { depth++; }
        else if ((c === ")" || c === "}") && depth > 0) { depth--; }
        i++;
      }
      tokens.push(text.slice(start, i));
    }
    return tokens;
  }

  function withoutQuotes(token) { return token.replace(/"[^"]*"/g, ""); }
  function isOrWord(token) { return /^(OR|ODER)$/i.test(token); }
  function quoted(value) {
    if (/^".*"$/.test(value)) { return value; }
    return /[\s(){}|"]/.test(value) ? '"' + value.replace(/"/g, "") + '"' : value;
  }

  /** key:value; several alternatives (written with OR) become key:(a OR b), a value with blanks a phrase. */
  function operator(key, text) {
    var value = (text || "").trim();
    if (!value) { return null; }
    if (/^\(.*\)$/.test(value)) { value = value.slice(1, -1).trim(); }
    if (/\s(OR|ODER)\s/i.test(value) || value.indexOf("|") >= 0) { return key + ":(" + value + ")"; }
    return key + ":" + quoted(value);
  }

  function number(text) {
    var value = (text || "").trim().replace(",", ".");
    return /^\d+(\.\d+)?$/.test(value) && parseFloat(value) > 0 ? value : null;
  }

  function composeQuery(v) {
    var parts = [];
    [operator("from", v.from), operator("to", v.to), operator("subject", v.subject)].forEach(function (part) { if (part) { parts.push(part); } });

    var words = (v.words || "").trim();
    var others = [];
    tokenize(v.without || "").forEach(function (word) { if (!isOrWord(word) && word !== "|") { others.push("-" + word); } });
    if (v.folder) { others.push("in:" + quoted(v.folder)); }
    if (v.within) { others.push("newer_than:" + v.within); }
    if (v.after) { others.push("after:" + v.after); }
    if (v.before) { others.push("before:" + v.before); }
    if (number(v.larger)) { others.push("larger:" + number(v.larger) + "M"); }
    if (number(v.smaller)) { others.push("smaller:" + number(v.smaller) + "M"); }
    if (v.attachment) { others.push("has:attachment"); }
    if (v.unread) { others.push("is:unread"); }
    if (v.starred) { others.push("is:starred"); }

    if (words) {
      // The fields narrow the words down (AND); a word text with OR in it must not swallow them.
      var alternatives = tokenize(words).some(function (t) { return isOrWord(t) || withoutQuotes(t).indexOf("|") >= 0; });
      parts.push(alternatives && (parts.length || others.length) ? "(" + words + ")" : words);
    }
    return parts.concat(others).join(" ");
  }

  function unquote(raw) { return /^"[^"]*"$/.test(raw) ? raw.slice(1, -1) : raw; }

  /** The value of a field when the text for it is plain (a word, a phrase, or alternatives with OR); otherwise null. */
  function simpleValue(raw) {
    var group = /^\((.*)\)$/.exec(raw);
    if (group) {
      var tokens = tokenize(group[1].trim());
      if (tokens.length < 3 || tokens.length % 2 === 0) { return null; }
      for (var i = 0; i < tokens.length; i++) {
        var between = i % 2 === 1;
        if (between !== isOrWord(tokens[i])) { return null; }
        if (!between && /[(){}|]/.test(withoutQuotes(tokens[i]))) { return null; }
      }
      return tokens.join(" ");
    }
    if (/^"[^"]*"$/.test(raw)) { return raw.slice(1, -1); }
    return /[()"{}|]/.test(raw) ? null : raw;
  }

  function pad(value) { return ("0" + value).slice(-2); }

  function isoDate(raw) {
    var iso = /^(\d{4})[-\/](\d{1,2})[-\/](\d{1,2})$/.exec(raw);
    var german = /^(\d{1,2})\.(\d{1,2})\.(\d{4})$/.exec(raw);
    var year, month, day;
    if (iso) { year = +iso[1]; month = +iso[2]; day = +iso[3]; }
    else if (german) { day = +german[1]; month = +german[2]; year = +german[3]; }
    else { return null; }
    var check = new Date(year, month - 1, day);
    if (check.getFullYear() !== year || check.getMonth() !== month - 1 || check.getDate() !== day) { return null; }
    return year + "-" + pad(month) + "-" + pad(day);
  }

  function megabytes(raw) {
    var match = /^(\d+(?:[.,]\d+)?)\s*(k|kb|m|mb|g|gb)?$/i.exec(raw);
    if (!match) { return null; }
    var factor = { "": 1 / 1048576, k: 1 / 1024, kb: 1 / 1024, m: 1, mb: 1, g: 1024, gb: 1024 }[(match[2] || "").toLowerCase()];
    var size = parseFloat(match[1].replace(",", ".")) * factor;
    return size >= 0.01 ? String(Math.round(size * 100) / 100) : null;
  }

  function folderValue(raw) {
    var value = unquote(raw).trim();
    var lower = value.toLowerCase();
    var word = FOLDER_ALIASES[lower] || lower;
    if (SYSTEM_WORDS.indexOf(word) >= 0) { return word; }
    var hit = folderOptions().filter(function (o) { return o[0] && o[0].toLowerCase() === lower; })[0];
    return hit ? hit[0] : null;
  }

  /** Puts a piece into its field when that is free and the piece fits; false leaves it with the words. */
  function absorb(values, key, raw) {
    switch (key) {
      case "from": case "to": case "subject":
        var value = simpleValue(raw);
        if (value === null || values[key]) { return false; }
        values[key] = value;
        return true;
      case "has":
        if (ATTACHMENT_WORDS.indexOf(raw.toLowerCase()) < 0 || values.attachment) { return false; }
        values.attachment = true;
        return true;
      case "is":
        var state = raw.toLowerCase();
        if ((state === "unread" || state === "ungelesen") && !values.unread) { values.unread = true; return true; }
        if ((state === "starred" || state === "markiert" || state === "stern") && !values.starred) { values.starred = true; return true; }
        return false;
      case "in":
        var folder = folderValue(raw);
        if (!folder || values.folder) { return false; }
        values.folder = folder;
        return true;
      case "newer_than":
        var within = raw.toLowerCase();
        if (!/^\d{1,4}[dwmy]$/.test(within) || values.within) { return false; }   // not only the presets: write() adds an entry for the others
        values.within = within;
        return true;
      case "after": case "before":
        var date = isoDate(raw);
        if (!date || values[key]) { return false; }
        values[key] = date;
        return true;
      case "larger": case "smaller":
        var size = megabytes(raw);
        if (!size || values[key]) { return false; }
        values[key] = size;
        return true;
      default:
        return false;
    }
  }

  function parseQuery(text) {
    var values = { from: "", to: "", subject: "", words: "", without: "", folder: "", within: "", after: "", before: "", larger: "", smaller: "", attachment: false, unread: false, starred: false };
    var tokens = tokenize(text || "");
    // With OR or brackets between the pieces their order matters: everything stays one text then.
    var tangled = tokens.some(function (t) {
      var body = withoutQuotes(t.charAt(0) === "-" ? t.slice(1) : t);
      return isOrWord(t) || /^[({]/.test(body) || /^[^:]*\|/.test(body);
    });
    if (tangled) { values.words = (text || "").trim(); return values; }

    var rest = [], without = [];
    tokens.forEach(function (token) {
      var negated = token.charAt(0) === "-" && token.length > 1;
      var body = negated ? token.slice(1) : token;
      var match = /^([^\s:"(){}|]+):(.+)$/.exec(body);
      if (!negated && match && KEYS[match[1].toLowerCase()] && absorb(values, KEYS[match[1].toLowerCase()], match[2])) { return; }
      if (negated && !match && !/[(){}|]/.test(withoutQuotes(body))) { without.push(body); return; }
      rest.push(token);
    });
    values.words = rest.join(" ");
    values.without = without.join(" ");
    return values;
  }

  // ---------------------------------------------------------------------------------------------
  // The panel
  // ---------------------------------------------------------------------------------------------
  function folderOptions() {
    var options = [["", T("everywhereExceptJunk")], ["anywhere", T("everywhereAll")]];
    var box = App.boot ? (App.mailbox(S.mailboxId) || App.boot.mailboxes[0]) : null;
    if (box) {
      box.folders.forEach(function (folder) {
        var value = folder.kind === "Custom" ? folder.path : FOLDER_WORDS[folder.kind];
        if (value) { options.push([value, new Array((folder.depth || 0) + 1).join("  ") + App.folderLabel(folder)]); }
      });
    }
    return options;
  }

  function field(label, control, size) {
    var id = "search-field-" + (++counter);
    control.id = id;
    return App.el("div", { class: "form-row search-panel__field" + (size ? " search-panel__field--" + size : "") }, [App.el("label", { for: id, text: label }), control]);
  }

  function textField(name, label) {
    fields[name] = App.el("input", { type: "text", class: "form-control", autocomplete: "off", autocapitalize: "off", spellcheck: "false", enterkeyhint: "search" });
    return field(label, fields[name], "wide");
  }

  function dateField(name, label) {
    fields[name] = App.el("input", { type: "date", class: "form-control" });
    return field(label, fields[name]);
  }

  function sizeField(name, label) {
    fields[name] = App.el("input", { type: "number", class: "form-control", min: "0", step: "any", inputmode: "decimal" });
    return field(label, fields[name]);
  }

  function checkField(name, label) {
    var id = "search-field-" + (++counter);
    fields[name] = App.el("input", { type: "checkbox", id: id });
    return App.el("div", { class: "form-check" }, [fields[name], App.el("label", { for: id, text: label })]);
  }

  function build() {
    if (panel) { return; }
    fields.folder = App.el("select", { class: "form-control" });
    fields.within = App.el("select", { class: "form-control" });

    var body = App.el("div", { class: "search-panel__body" }, [
      textField("from", T("from")),
      textField("to", T("to")),
      textField("subject", T("subject")),
      field(T("searchIn"), fields.folder, "wide"),
      textField("words", T("hasWords")),
      textField("without", T("notWords")),
      field(T("dateWithin"), fields.within, "wide"),
      dateField("after", T("receivedAfter")),
      dateField("before", T("receivedBefore")),
      sizeField("larger", T("largerThanMb")),
      sizeField("smaller", T("smallerThanMb")),
      App.el("div", { class: "search-panel__checks search-panel__field--full" }, [checkField("attachment", T("hasAttachment")), checkField("unread", T("unread")), checkField("starred", T("starred"))]),
      App.el("p", { class: "form-help search-panel__field--full", text: T("searchTip") })
    ]);

    var search = App.el("button", { type: "submit", class: "btn btn--primary", text: T("searchButton") });
    var reset = App.el("button", { type: "button", class: "btn btn--secondary", text: T("reset") });
    reset.addEventListener("click", function () { write(parseQuery("")); fields.from.focus(); });
    var footer = App.el("div", { class: "search-panel__footer" }, [search, reset]);

    var form = App.el("form", { class: "search-panel__form", autocomplete: "off" }, [body, footer]);
    form.addEventListener("submit", function (e) {
      e.preventDefault();
      var query = composeQuery(read());
      ctx.input.value = query;
      close(false);
      ctx.run(query);
    });

    var header = App.el("div", { class: "search-panel__header" }, [
      App.el("span", { class: "search-panel__title", text: T("advancedSearch") }),
      App.iconButton("x", T("close"), function () { close(true); })
    ]);
    panel = App.el("div", { class: "search-panel", role: "dialog", "aria-label": T("advancedSearch") }, [header, form]);
    layer.appendChild(panel);
  }

  /** The choices depend on the mailbox (its folders) and on the text (a period of its own, like 7d, is added to the presets). */
  function fillChoices() {
    fields.folder.innerHTML = "";
    folderOptions().forEach(function (o) { fields.folder.appendChild(App.el("option", { value: o[0], text: o[1] })); });
    fields.within.innerHTML = "";
    WITHIN.forEach(function (w) { fields.within.appendChild(App.el("option", { value: w[0], text: T(w[1]) })); });
  }

  function read() {
    var v = {};
    ["from", "to", "subject", "words", "without", "folder", "within", "after", "before", "larger", "smaller"].forEach(function (name) { v[name] = fields[name].value; });
    ["attachment", "unread", "starred"].forEach(function (name) { v[name] = fields[name].checked; });
    return v;
  }

  function write(v) {
    if (v.within && !Array.prototype.some.call(fields.within.options, function (o) { return o.value === v.within; })) {
      fields.within.appendChild(App.el("option", { value: v.within, text: v.within }));
    }
    ["from", "to", "subject", "words", "without", "folder", "within", "after", "before", "larger", "smaller"].forEach(function (name) { fields[name].value = v[name]; });
    ["attachment", "unread", "starred"].forEach(function (name) { fields[name].checked = v[name]; });
  }

  /** Under the search box on a wide screen; phones get a full-screen sheet from the style sheet. */
  function place() {
    if (!window.matchMedia("(min-width: 721px)").matches) { panel.style.left = panel.style.top = panel.style.width = ""; return; }
    var rect = ctx.wrap.getBoundingClientRect();
    var width = Math.min(Math.max(rect.width, 640), window.innerWidth - 16);
    panel.style.width = width + "px";
    panel.style.left = Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)) + "px";
    panel.style.top = (rect.bottom + 6) + "px";
  }

  function show() {
    build();
    fillChoices();
    write(parseQuery(ctx.input.value));
    layer.hidden = false;
    isOpen = true;
    ctx.options.setAttribute("aria-expanded", "true");
    place();
    fields.from.focus();
  }

  function close(refocus) {
    if (!isOpen) { return; }
    isOpen = false;
    layer.hidden = true;
    ctx.options.setAttribute("aria-expanded", "false");
    if (refocus) { ctx.options.focus(); }
  }

  /** wrap: the search box, input: its text field, options: the button that opens the panel, run(text): starts the search. */
  function attach(options) {
    ctx = options;
    layer = doc.getElementById("mail-search-layer");
    ctx.options.addEventListener("click", function () { if (isOpen) { close(true); } else { show(); } });
    doc.addEventListener("mousedown", function (e) {
      if (isOpen && !panel.contains(e.target) && !ctx.wrap.contains(e.target)) { close(false); }
    }, true);
    doc.addEventListener("keydown", function (e) { if (isOpen && e.key === "Escape") { e.stopPropagation(); close(true); } }, true);
    window.addEventListener("resize", function () { if (isOpen) { place(); } });   // not "close": the keyboard of a phone resizes the page
  }

  App.search = { attach: attach, close: function () { close(false); }, compose: composeQuery, parse: parseQuery };
})();
