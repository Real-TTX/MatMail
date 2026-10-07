// MatMail – a small rich text editor for signatures and templates: a contenteditable area with a toolbar, pictures, placeholders and a
// switch to the HTML source. It keeps the <textarea> it replaces up to date, so the form posts like any other.
//
// It is the behaviour of <mm-field kind="richtext">: every textarea[data-rich-text] gets an editor when the page is loaded
// (data-placeholders: comma separated names, data-texts: the toolbar texts as JSON). A preview frame ([data-rte-preview] in the same
// row, data-sample: { values, body }) shows the HTML with the placeholders filled in. Also usable by hand:
//
//   MatRte.attach(textarea, { placeholders: ["DisplayName", ...], texts: { bold: "Bold", ... }, preview: iframe, sample: { values: {}, body: "" } })
(function () {
  "use strict";

  var doc = document;
  var MAX_IMAGE_BYTES = 300 * 1024;
  var MAX_IMAGE_WIDTH = 640;

  window.MatRte = { attach: attach };

  function el(tag, attributes, children) {
    var node = doc.createElement(tag);
    Object.keys(attributes || {}).forEach(function (key) {
      if (key === "text") { node.textContent = attributes[key]; }
      else if (attributes[key] !== false && attributes[key] !== null && attributes[key] !== undefined) { node.setAttribute(key, attributes[key]); }
    });
    (children || []).forEach(function (child) { node.appendChild(child); });
    return node;
  }

  function icon(name) {
    var svg = doc.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", "icon");
    svg.setAttribute("aria-hidden", "true");
    var use = doc.createElementNS("http://www.w3.org/2000/svg", "use");
    use.setAttribute("href", "#i-" + name);
    svg.appendChild(use);
    return svg;
  }

  function attach(textarea, options) {
    options = options || {};
    var texts = options.texts || {};
    var T = function (key, fallback) { return texts[key] || fallback; };
    var placeholders = options.placeholders || [];

    var editor = el("div", { class: "rte__editor", contenteditable: "true", role: "textbox", "aria-multiline": "true", "aria-label": T("content", "Content"), spellcheck: "true" });
    var toolbar = el("div", { class: "rte__toolbar", role: "toolbar" });
    var imageBar = buildImageBar();
    var root = el("div", { class: "rte" }, [toolbar, imageBar.node, editor]);
    var fileInput = el("input", { type: "file", accept: "image/png,image/jpeg,image/gif,image/webp", hidden: "hidden" });
    var savedRange = null;
    var sourceMode = false;
    var selectedImage = null;

    textarea.parentNode.insertBefore(root, textarea);
    root.appendChild(fileInput);
    root.appendChild(textarea);
    textarea.hidden = true;
    textarea.classList.add("rte__source");
    // Whatever the textarea holds is shown as live markup: nothing active may come along (the server cleans it again when it is saved).
    editor.innerHTML = clean(textarea.value);

    // ---- toolbar ---------------------------------------------------------------------------------
    function button(name, title, run, label) {
      var b = el("button", { type: "button", class: "rte__button", title: title, "aria-label": title });
      if (label) { b.textContent = label; } else { b.appendChild(icon(name)); }
      b.addEventListener("mousedown", function (e) { e.preventDefault(); });
      b.addEventListener("click", function () { if (!sourceMode) { restoreRange(); run(); sync(); } });
      toolbar.appendChild(b);
      return b;
    }

    function separator() { toolbar.appendChild(el("span", { class: "rte__separator", "aria-hidden": "true" })); }

    function command(name, value) { doc.execCommand(name, false, value === undefined ? null : value); }

    button("bold", T("bold", "Bold"), function () { command("bold"); });
    button("italic", T("italic", "Italic"), function () { command("italic"); });
    button("underline", T("underline", "Underline"), function () { command("underline"); });
    separator();

    var size = el("select", { class: "rte__select", title: T("size", "Text size"), "aria-label": T("size", "Text size") }, [
      el("option", { value: "", text: T("size", "Text size") }),
      el("option", { value: "1", text: T("sizeSmall", "Small") }),
      el("option", { value: "3", text: T("sizeNormal", "Normal") }),
      el("option", { value: "5", text: T("sizeLarge", "Large") }),
      el("option", { value: "6", text: T("sizeHuge", "Very large") })
    ]);
    size.addEventListener("change", function () { if (size.value) { restoreRange(); command("fontSize", size.value); sync(); } size.value = ""; });
    toolbar.appendChild(size);

    var color = el("input", { type: "color", class: "rte__color", value: "#202124", title: T("color", "Text colour"), "aria-label": T("color", "Text colour") });
    color.addEventListener("input", function () { restoreRange(); command("foreColor", color.value); sync(); });
    toolbar.appendChild(color);
    separator();

    button("list", T("bulletList", "Bulleted list"), function () { command("insertUnorderedList"); });
    button("list-ordered", T("numberList", "Numbered list"), function () { command("insertOrderedList"); });
    button("link", T("link", "Link"), function () {
      var url = window.prompt(T("linkPrompt", "Web address (or mailto:)"), "https://");
      if (url) { command("createLink", url); }
    });
    button("image", T("picture", "Insert picture"), function () { fileInput.click(); });
    button("minus", T("rule", "Horizontal line"), function () { command("insertHorizontalRule"); });
    button("type", T("clear", "Clear formatting"), function () { command("removeFormat"); command("unlink"); });
    separator();

    if (placeholders.length) {
      var holder = el("select", { class: "rte__select", title: T("placeholder", "Insert placeholder"), "aria-label": T("placeholder", "Insert placeholder") },
        [el("option", { value: "", text: T("placeholder", "Insert placeholder") })].concat(placeholders.map(function (name) { return el("option", { value: name, text: "{{" + name + "}}" }); })));
      holder.addEventListener("change", function () {
        if (holder.value) { restoreRange(); command("insertText", "{{" + holder.value + "}}"); sync(); }
        holder.value = "";
      });
      toolbar.appendChild(holder);
    }

    var sourceButton = button("", T("source", "HTML source"), function () {}, "</>");
    sourceButton.addEventListener("click", toggleSource);

    // ---- selection --------------------------------------------------------------------------------
    function saveRange() {
      var selection = window.getSelection();
      if (selection.rangeCount && editor.contains(selection.anchorNode)) { savedRange = selection.getRangeAt(0).cloneRange(); }
    }

    function restoreRange() {
      editor.focus();
      if (savedRange) {
        var selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(savedRange);
      }
    }

    ["keyup", "mouseup", "blur", "input"].forEach(function (name) { editor.addEventListener(name, saveRange); });

    // ---- sync with the textarea ---------------------------------------------------------------------
    /** The markup of the editor as it is stored: without the marks the editor itself puts on (the selected picture). */
    function serialize() {
      var copy = editor.cloneNode(true);
      Array.prototype.forEach.call(copy.querySelectorAll("img.is-selected"), function (picture) {
        picture.classList.remove("is-selected");
        if (!picture.getAttribute("class")) { picture.removeAttribute("class"); }
      });
      return copy.innerHTML;
    }

    function sync() {
      if (sourceMode) { return; }
      textarea.value = serialize();
      textarea.dispatchEvent(new Event("input", { bubbles: true }));
    }

    editor.addEventListener("input", sync);

    function toggleSource() {
      sourceMode = !sourceMode;
      root.classList.toggle("is-source", sourceMode);
      if (sourceMode) {
        textarea.value = serialize();
        textarea.hidden = false;
        editor.hidden = true;
        hideImageBar();
        textarea.focus();
      } else {
        editor.innerHTML = clean(textarea.value);
        textarea.hidden = true;
        editor.hidden = false;
        editor.focus();
        sync();
      }
      sourceButton.setAttribute("aria-pressed", sourceMode ? "true" : "false");
    }

    textarea.addEventListener("input", function () { /* the source view is the master while it is shown */ });

    // ---- pictures -------------------------------------------------------------------------------------
    fileInput.addEventListener("change", function () {
      Array.prototype.forEach.call(fileInput.files, insertPicture);
      fileInput.value = "";
    });

    function insertPicture(file) {
      if (!/^image\/(png|jpeg|gif|webp)$/.test(file.type)) { complain(T("pictureType", "Only PNG, JPEG, GIF and WebP pictures can be inserted.")); return; }
      var reader = new FileReader();
      reader.onload = function () {
        var image = new Image();
        image.onload = function () {
          var data = shrink(image, file.type);
          if (!data) { complain(T("pictureTooLarge", "The picture is too large, even after shrinking it.")); return; }
          restoreRange();
          command("insertImage", data);
          sync();
        };
        image.onerror = function () { complain(T("pictureType", "Only PNG, JPEG, GIF and WebP pictures can be inserted.")); };
        image.src = reader.result;
      };
      reader.readAsDataURL(file);
    }

    /** Scales the picture down (and, if need be, further) until it is small enough to live inside the signature. */
    function shrink(image, type) {
      var keepTransparency = type !== "image/jpeg";
      var width = Math.min(image.naturalWidth, MAX_IMAGE_WIDTH);
      for (var attempt = 0; attempt < 8; attempt++) {
        var height = Math.max(1, Math.round(image.naturalHeight * width / image.naturalWidth));
        var canvas = doc.createElement("canvas");
        canvas.width = width;
        canvas.height = height;
        var context = canvas.getContext("2d");
        if (!keepTransparency) { context.fillStyle = "#ffffff"; context.fillRect(0, 0, width, height); }
        context.drawImage(image, 0, 0, width, height);
        var data = keepTransparency ? canvas.toDataURL("image/png") : canvas.toDataURL("image/jpeg", 0.88);
        if (data.length * 0.75 <= MAX_IMAGE_BYTES) { return data; }
        if (keepTransparency && attempt === 2) { keepTransparency = false; /* PNG photographs are huge; a JPEG is the better trade */ }
        width = Math.round(width * 0.8);
        if (width < 60) { break; }
      }
      return null;
    }

    function complain(message) { if (options.onError) { options.onError(message); } else { window.alert(message); } }

    editor.addEventListener("paste", function (e) {
      var data = e.clipboardData;
      if (!data) { return; }
      var files = Array.prototype.filter.call(data.files || [], function (f) { return /^image\//.test(f.type); });
      if (files.length) { e.preventDefault(); files.forEach(insertPicture); return; }
      var html = data.getData("text/html");
      if (html) { e.preventDefault(); command("insertHTML", clean(html)); sync(); }
    });

    editor.addEventListener("dragover", function (e) { if (e.dataTransfer && Array.prototype.some.call(e.dataTransfer.types || [], function (t) { return t === "Files"; })) { e.preventDefault(); } });
    editor.addEventListener("drop", function (e) {
      var files = Array.prototype.filter.call((e.dataTransfer && e.dataTransfer.files) || [], function (f) { return /^image\//.test(f.type); });
      if (files.length) { e.preventDefault(); files.forEach(insertPicture); }
    });

    /** Pasted HTML (Word, web pages) without scripts, styles of the source and Office leftovers. */
    function clean(html) {
      var parsed = new DOMParser().parseFromString(html, "text/html");
      parsed.querySelectorAll("script,style,meta,link,title,xml,object,embed,iframe,frame,frameset,applet,base,form,noscript,template").forEach(function (node) { node.remove(); });
      parsed.querySelectorAll("*").forEach(function (node) {
        Array.prototype.slice.call(node.attributes).forEach(function (attribute) {
          var name = attribute.name.toLowerCase();
          var value = attribute.value || "";
          if (name.indexOf("on") === 0 || name === "class" || name === "lang" || /^\s*javascript:/i.test(value)) { node.removeAttribute(attribute.name); }
          else if (name === "style") {
            var kept = value.split(";").filter(function (rule) { return rule.trim() && !/^\s*(mso-|position|z-index)/i.test(rule); }).join(";");
            if (kept) { node.setAttribute("style", kept); } else { node.removeAttribute("style"); }
          }
        });
      });
      return parsed.body ? parsed.body.innerHTML : "";
    }

    // ---- selected picture: width and removal -----------------------------------------------------------
    function buildImageBar() {
      var width = el("input", { type: "number", min: "16", max: "1200", class: "form-control rte__width", "aria-label": T("pictureWidth", "Width in pixels") });
      var remove = el("button", { type: "button", class: "btn btn--secondary btn--sm", text: T("pictureRemove", "Remove picture") });
      var node = el("div", { class: "rte__imagebar", hidden: "hidden" }, [
        el("label", { text: T("pictureWidth", "Width in pixels") }), width, remove
      ]);
      width.addEventListener("input", function () {
        if (selectedImage && width.value) { selectedImage.setAttribute("width", width.value); selectedImage.removeAttribute("height"); selectedImage.style.width = ""; sync(); }
      });
      remove.addEventListener("click", function () { if (selectedImage) { selectedImage.remove(); hideImageBar(); sync(); } });
      return { node: node, width: width };
    }

    function hideImageBar() {
      if (selectedImage) { selectedImage.classList.remove("is-selected"); }
      selectedImage = null;
      imageBar.node.hidden = true;
    }

    editor.addEventListener("click", function (e) {
      if (e.target && e.target.tagName === "IMG") {
        if (selectedImage) { selectedImage.classList.remove("is-selected"); }
        selectedImage = e.target;
        selectedImage.classList.add("is-selected");
        imageBar.width.value = selectedImage.getAttribute("width") || String(selectedImage.naturalWidth || selectedImage.width);
        imageBar.node.hidden = false;
      } else {
        hideImageBar();
      }
    });

    // The form reads the textarea: make sure it holds the latest.
    var form = textarea.closest("form");
    if (form) { form.addEventListener("submit", function () { if (!sourceMode) { textarea.value = serialize(); } }); }

    // ---- preview ------------------------------------------------------------------------------------------
    var preview = options.preview;
    var sample = options.sample || {};

    function escapeHtml(text) { return String(text).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;"); }

    /** The HTML with the placeholders replaced by the sample values ({{Body}}: the sample message, which is markup). */
    function renderPreview() {
      if (!preview) { return; }
      var values = sample.values || {};
      var html = textarea.value.replace(/\{\{\s*(\w+)\s*\}\}/g, function (found, key) {
        if (key.toLowerCase() === "body") { return sample.body || ""; }
        var name = Object.keys(values).filter(function (candidate) { return candidate.toLowerCase() === key.toLowerCase(); })[0];
        return name ? escapeHtml(values[name]) : found;
      });
      preview.srcdoc = '<!doctype html><meta charset="utf-8"><body style="font:14px/1.5 system-ui,sans-serif;margin:8px;color:#202124">' + html;
    }

    textarea.addEventListener("input", renderPreview);
    renderPreview();

    return { sync: sync, editor: editor };
  }

  function parseJson(text, fallback) {
    try { return text ? JSON.parse(text) : fallback; } catch (e) { return fallback; }
  }

  function attachAll() {
    Array.prototype.forEach.call(doc.querySelectorAll("textarea[data-rich-text]"), function (area) {
      if (area.getAttribute("data-rte-ready")) { return; }
      area.setAttribute("data-rte-ready", "true");
      var row = area.closest(".form-row") || area.parentNode;
      var frame = row.querySelector("[data-rte-preview]");
      attach(area, {
        placeholders: (area.getAttribute("data-placeholders") || "").split(",").map(function (name) { return name.trim(); }).filter(Boolean),
        texts: parseJson(area.getAttribute("data-texts"), {}),
        preview: frame,
        sample: frame ? parseJson(frame.getAttribute("data-sample"), {}) : null
      });
    });
  }

  if (doc.readyState === "loading") { doc.addEventListener("DOMContentLoaded", attachAll); } else { attachAll(); }
})();
