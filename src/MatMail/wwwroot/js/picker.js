// MatMail – picker: searchable single/multi select in a dialog (full screen on phones, styled in components.css).
// The values travel in hidden inputs that PickerTagHelper renders on the server, so posting works like a plain <select>.
(function () {
  "use strict";

  var doc = document;
  var supportsDialog = typeof HTMLDialogElement !== "undefined" && typeof HTMLDialogElement.prototype.showModal === "function";

  function ready(fn) { if (doc.readyState === "loading") { doc.addEventListener("DOMContentLoaded", fn); } else { fn(); } }

  ready(function () {
    if (!supportsDialog) { return; }
    var widgets = Array.prototype.slice.call(doc.querySelectorAll(".picker"));
    if (!widgets.length) { return; }

    var dialog = buildDialog(widgets[0]);
    doc.body.appendChild(dialog);
    widgets.forEach(function (w) { wire(w, dialog); });
  });

  function buildDialog(labels) {
    var dialog = doc.createElement("dialog");
    dialog.className = "picker-dialog";

    var header = doc.createElement("div");
    header.className = "picker-dialog__header";
    var search = doc.createElement("input");
    search.type = "search";
    search.className = "form-control picker-dialog__search";
    search.placeholder = labels.getAttribute("data-search-label") || "Search…";
    search.autocomplete = "off";
    var close = doc.createElement("button");
    close.type = "button";
    close.className = "icon-btn";
    close.setAttribute("aria-label", labels.getAttribute("data-close-label") || "Close");
    close.innerHTML = '<svg class="icon" aria-hidden="true"><use href="#i-x"/></svg>';
    header.appendChild(search);
    header.appendChild(close);

    var list = doc.createElement("ul");
    list.className = "picker-dialog__list";

    var footer = doc.createElement("div");
    footer.className = "picker-dialog__footer";
    var done = doc.createElement("button");
    done.type = "button";
    done.className = "btn btn--primary";
    done.textContent = labels.getAttribute("data-done-label") || "Done";
    footer.appendChild(done);

    dialog.appendChild(header);
    dialog.appendChild(list);
    dialog.appendChild(footer);
    dialog._p = { search: search, list: list, footer: footer, done: done, active: null };

    close.addEventListener("click", function () { dialog._p.active = null; dialog.close(); });
    search.addEventListener("input", function () { render(dialog, search.value); });
    dialog.addEventListener("click", function (e) { if (e.target === dialog) { dialog.close(); } });
    dialog.addEventListener("cancel", function () { dialog._p.active = null; });
    done.addEventListener("click", function () { applyMultiple(dialog); dialog.close(); });
    return dialog;
  }

  function wire(widget, dialog) {
    Array.prototype.forEach.call(widget.querySelectorAll(".picker__trigger"), function (t) {
      t.addEventListener("click", function () { open(widget, dialog); });
    });
    var chips = widget.querySelector(".picker__chips");
    if (chips) {
      chips.addEventListener("click", function (e) {
        var btn = e.target.closest(".picker__chip-remove");
        if (!btn) { return; }
        var chip = btn.closest(".picker__chip");
        if (chip) { chip.remove(); changed(widget); }
      });
    }
  }

  function options(widget) {
    var script = widget.querySelector(".picker__data");
    try { return script ? JSON.parse(script.textContent) || [] : []; } catch (e) { return []; }
  }

  function selection(widget, multiple) {
    var set = Object.create(null);
    if (multiple) {
      Array.prototype.forEach.call(widget.querySelectorAll(".picker__chip input[type=hidden]"), function (i) { set[i.value] = true; });
    } else {
      var hidden = widget.querySelector(".picker__value");
      if (hidden && hidden.value !== "") { set[hidden.value] = true; }
    }
    return set;
  }

  function open(widget, dialog) {
    var p = dialog._p;
    var multiple = widget.getAttribute("data-multiple") === "true";
    p.active = { widget: widget, multiple: multiple, options: options(widget), selected: selection(widget, multiple), placeholder: widget.getAttribute("data-placeholder") || "" };
    p.search.placeholder = widget.getAttribute("data-search-label") || p.search.placeholder;
    p.done.textContent = widget.getAttribute("data-done-label") || p.done.textContent;
    p.footer.style.display = multiple ? "" : "none";
    p.search.value = "";
    render(dialog, "");
    dialog.showModal();
    p.search.focus();
  }

  function render(dialog, filter) {
    var p = dialog._p, a = p.active;
    if (!a) { return; }
    var needle = (filter || "").toLowerCase();
    p.list.innerHTML = "";
    if (!a.multiple) { p.list.appendChild(singleRow(dialog, { v: "", t: a.placeholder, clear: true }, Object.keys(a.selected).length === 0)); }
    a.options.forEach(function (o) {
      if (needle && o.t.toLowerCase().indexOf(needle) === -1) { return; }
      var sel = a.selected[o.v] === true;
      p.list.appendChild(a.multiple ? multiRow(a, o, sel) : singleRow(dialog, o, sel));
    });
  }

  function singleRow(dialog, o, sel) {
    var li = doc.createElement("li");
    li.className = "picker-dialog__item" + (sel ? " is-selected" : "") + (o.clear ? " picker-dialog__item--clear" : "");
    li.textContent = o.t;
    li.addEventListener("click", function () { commitSingle(dialog, o); dialog.close(); });
    return li;
  }

  function multiRow(a, o, sel) {
    var li = doc.createElement("li");
    li.className = "picker-dialog__item" + (sel ? " is-selected" : "");
    var label = doc.createElement("label");
    label.className = "picker-dialog__check";
    var cb = doc.createElement("input");
    cb.type = "checkbox";
    cb.checked = sel;
    cb.addEventListener("change", function () {
      if (cb.checked) { a.selected[o.v] = true; } else { delete a.selected[o.v]; }
      li.classList.toggle("is-selected", cb.checked);
    });
    var text = doc.createElement("span");
    text.textContent = o.t;
    label.appendChild(cb);
    label.appendChild(text);
    li.appendChild(label);
    return li;
  }

  function changed(widget) { widget.dispatchEvent(new Event("change", { bubbles: true })); }

  function commitSingle(dialog, o) {
    var a = dialog._p.active;
    if (!a) { return; }
    var widget = a.widget;
    var hidden = widget.querySelector(".picker__value");
    var trigger = widget.querySelector(".picker__trigger");
    var label = trigger ? trigger.querySelector(".picker__label") : null;
    var value = o.clear ? "" : o.v;
    if (hidden) { hidden.value = value; }
    if (label) { label.textContent = value === "" ? a.placeholder : o.t; }
    if (trigger) { if (value === "") { trigger.setAttribute("data-placeholder-shown", "true"); } else { trigger.removeAttribute("data-placeholder-shown"); } }
    dialog._p.active = null;
    changed(widget);
  }

  function applyMultiple(dialog) {
    var a = dialog._p.active;
    if (!a || !a.multiple) { return; }
    var widget = a.widget;
    var chips = widget.querySelector(".picker__chips");
    if (!chips) { return; }
    var name = widget.getAttribute("data-name") || "";
    chips.innerHTML = "";
    a.options.filter(function (o) { return a.selected[o.v] === true; }).forEach(function (o) {
      var chip = doc.createElement("span");
      chip.className = "picker__chip";
      chip.setAttribute("data-value", o.v);
      var hidden = doc.createElement("input");
      hidden.type = "hidden"; hidden.name = name; hidden.value = o.v;
      var text = doc.createElement("span");
      text.className = "picker__chip-text"; text.textContent = o.t;
      var remove = doc.createElement("button");
      remove.type = "button"; remove.className = "picker__chip-remove"; remove.tabIndex = -1; remove.textContent = "×";
      remove.setAttribute("aria-label", widget.getAttribute("data-remove-label") || "Remove");
      chip.appendChild(hidden); chip.appendChild(text); chip.appendChild(remove);
      chips.appendChild(chip);
    });
    dialog._p.active = null;
    changed(widget);
  }
})();
