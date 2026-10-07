// MatMail web client – the viewer: makes whatever mail arrives fit the reader.
// The mail itself is shown in a sandboxed iframe without scripts (see MailBodyRenderer); everything dynamic happens here, in the
// page around it: scaling wide layouts to the available width, the height of the frame, folding of quoted replies, the dark theme.
(function () {
  "use strict";

  var App = window.MailApp = window.MailApp || {};
  var frames = [];

  App.viewer = { attach: attach, refit: refitAll };

  /**
   * Looks after an iframe that shows a mail. Returns a handle: <code>fitting</code> tells whether wide layouts are scaled down to the
   * width (the default), <code>setFit(false)</code> shows them in their original size instead (the frame scrolls sideways).
   * The frame gets an "mm-fit" event with <code>detail.wide</code> (the layout is wider than the reader) and <code>detail.fitting</code>.
   */
  function attach(frame) {
    var viewer = { frame: frame, lastWidth: 0, fitting: true };
    frames.push(viewer);
    frame.addEventListener("load", function () { start(viewer); });
    if (window.ResizeObserver) {
      // The window or the sidebar changes the width: the layout is fitted again (height changes alone do not trigger it).
      new ResizeObserver(function () {
        if (frame.clientWidth !== viewer.lastWidth) { fit(viewer); }
      }).observe(frame);
    }

    return {
      get fitting() { return viewer.fitting; },
      setFit: function (on) { viewer.fitting = !!on; fit(viewer); }
    };
  }

  function start(viewer) {
    var doc = contentOf(viewer.frame);
    if (!doc || !doc.body) { viewer.frame.style.height = "600px"; return; }

    applyTheme(viewer);
    foldQuotes(viewer, doc);
    fit(viewer);

    // Pictures and fonts that arrive late change the size; so do the toggles of folded quotes.
    if (window.ResizeObserver) {
      new ResizeObserver(function () { resize(viewer); }).observe(doc.documentElement);
    }

    doc.addEventListener("load", function () { fit(viewer); }, true);
  }

  function contentOf(frame) {
    try { return frame.contentDocument; } catch (e) { return null; }
  }

  function isDark() { return document.documentElement.getAttribute("data-mode") === "dark"; }

  /** Mails without colours of their own follow the dark theme; the ones with colours stay on a light page. */
  function applyTheme(viewer) {
    var doc = contentOf(viewer.frame);
    if (!doc) { return; }
    var root = doc.documentElement;
    var dark = isDark();
    root.classList.toggle("mm-dark", dark);
    viewer.frame.classList.toggle("reader__body--paper", dark && root.classList.contains("mm-styled"));
  }

  function refitAll() {
    frames.forEach(function (viewer) { applyTheme(viewer); fit(viewer); });
  }

  // The theme switch of the page changes data-mode; the open mail follows.
  if (window.MutationObserver) {
    new MutationObserver(function () { frames.forEach(applyTheme); }).observe(document.documentElement, { attributes: true, attributeFilter: ["data-mode"] });
  }

  // ---------------------------------------------------------------------------------------------
  // Size
  // ---------------------------------------------------------------------------------------------

  /** Scales a layout that is wider than the reader (newsletters with a fixed 600 or 900 pixel table) down to fit. */
  function fit(viewer) {
    var frame = viewer.frame;
    var doc = contentOf(frame);
    if (!doc || !doc.body) { return; }
    var holder = doc.getElementById("mm-body") || doc.body;
    var available = frame.clientWidth;
    viewer.lastWidth = available;
    if (available <= 0) { return; }

    holder.style.zoom = "";
    var natural = Math.max(doc.documentElement.scrollWidth, holder.scrollWidth);
    var needed = natural;
    for (var pass = 0; viewer.fitting && pass < 3 && needed > available + 1; pass++) {
      var zoom = Math.max(0.25, available / needed * (pass === 0 ? 1 : 0.98));
      holder.style.zoom = String(zoom);
      needed = Math.max(doc.documentElement.scrollWidth, holder.scrollWidth);
      if (zoom <= 0.25) { break; }
    }

    resize(viewer);
    viewer.frame.dispatchEvent(new CustomEvent("mm-fit", { detail: { wide: natural > available + 1, fitting: viewer.fitting } }));
  }

  function resize(viewer) {
    var doc = contentOf(viewer.frame);
    if (!doc || !doc.documentElement) { return; }
    viewer.frame.style.height = Math.max(120, doc.documentElement.scrollHeight) + "px";
  }

  // ---------------------------------------------------------------------------------------------
  // Quoted replies
  // ---------------------------------------------------------------------------------------------

  /**
   * Replies carry the whole conversation below the new text. The quoted part is folded away behind a small "…" button (as Gmail does);
   * a mail that consists of nothing but a quote (a forwarded message) is left alone.
   */
  function foldQuotes(viewer, doc) {
    var holder = doc.getElementById("mm-body");
    if (!holder) { return; }
    var start = findQuoteStart(holder);
    if (!start) { return; }

    // What stays visible before the quote must be real text, otherwise there is nothing to fold it away from.
    if (!hasTextBefore(holder, start)) { return; }

    var parent = start.parentNode;
    var wrapper = doc.createElement("div");
    wrapper.className = "mm-quote";
    wrapper.hidden = true;
    var toggle = doc.createElement("button");
    toggle.type = "button";
    toggle.className = "mm-quote-toggle";
    toggle.textContent = "…";
    toggle.setAttribute("aria-expanded", "false");
    toggle.title = viewer.frame.getAttribute("data-quote-title") || "";

    var members = [start];
    // Outlook puts the header of the quoted message into a block and the quote follows as siblings.
    if (start.getAttribute && start.getAttribute("data-mm-quote-tail") === "1") {
      for (var next = start.nextSibling; next; next = next.nextSibling) { members.push(next); }
    }

    parent.insertBefore(toggle, start);
    parent.insertBefore(wrapper, start);
    members.forEach(function (node) { wrapper.appendChild(node); });

    toggle.addEventListener("click", function () {
      wrapper.hidden = !wrapper.hidden;
      toggle.setAttribute("aria-expanded", wrapper.hidden ? "false" : "true");
      fit(viewer);
    });
  }

  var HEADER_LABEL = /^(from|von|de|da|från|van|od|lähettäjä)s*:/i;

  function findQuoteStart(holder) {
    // Gmail, Apple Mail, Thunderbird: a block with a known class or type.
    var found = holder.querySelector("div.gmail_quote, blockquote.gmail_quote, blockquote[type='cite']");
    if (found) {
      // The attribution line ("On ... wrote:") sits right before it in Thunderbird.
      var before = found.previousElementSibling;
      return before && /moz-cite-prefix/.test(before.className || "") ? before : found;
    }

    // Outlook on the web: the header block and everything after it.
    var reply = holder.querySelector("#divRplyFwdMsg");
    if (reply) {
      var line = reply.previousElementSibling && reply.previousElementSibling.tagName === "HR" ? reply.previousElementSibling : reply;
      line.setAttribute("data-mm-quote-tail", "1");
      return line;
    }

    // Outlook (desktop, Word): a block with a thin line above lines like "From: ... Sent: ...", and the quote follows as siblings.
    var candidates = holder.querySelectorAll("div[style*='border-top']");
    for (var i = 0; i < candidates.length; i++) {
      if (HEADER_LABEL.test((candidates[i].textContent || "").trim())) {
        var block = candidates[i];
        // The block is usually wrapped in a div of its own that has the quote as following siblings.
        if (block.parentElement && block.parentElement !== holder && block.parentElement.children.length === 1 && !/WordSection/.test(block.parentElement.className)) {
          block = block.parentElement;
        }
        block.setAttribute("data-mm-quote-tail", "1");
        return block;
      }
    }

    // Plain text: the quoted lines ("> ...") were turned into blockquotes; the line that announces them stays visible.
    if (holder.ownerDocument.documentElement.classList.contains("mm-plain")) {
      var quote = holder.querySelector("blockquote");
      if (quote) { return quote; }
    }

    return null;
  }

  function hasTextBefore(holder, start) {
    var walker = holder.ownerDocument.createTreeWalker(holder, NodeFilter.SHOW_TEXT);
    var text = "";
    for (var node = walker.nextNode(); node; node = walker.nextNode()) {
      if (start.compareDocumentPosition(node) & Node.DOCUMENT_POSITION_FOLLOWING || start.contains(node)) { break; }
      text += node.textContent;
      if (text.trim().length >= 3) { return true; }
    }
    return text.trim().length >= 3;
  }
})();
