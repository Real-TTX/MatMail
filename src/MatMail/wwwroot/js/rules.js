// MatMail – the rule editor (Account → Mail rules → edit): conditions and actions are rows that can be added and removed;
// each row adjusts itself to its field or action. The rows are ordinary form fields named Input.Conditions[n].… and
// Input.Actions[n].…, so the page posts like any other; after every change the rows are numbered 0, 1, 2 … again.
(function () {
  "use strict";

  var doc = document;
  var form = doc.querySelector("form[data-rules]");
  if (!form) { return; }

  var operators = {};
  try { operators = JSON.parse(doc.getElementById("rule-operators").textContent); } catch (e) { /* the server rendered the options already */ }

  function $(selector, root) { return (root || doc).querySelector(selector); }
  function $$(selector, root) { return Array.prototype.slice.call((root || doc).querySelectorAll(selector)); }

  /** A control that is not in use is hidden and disabled: it neither shows nor posts. */
  function show(control, on) {
    if (!control) { return; }
    control.hidden = !on;
    control.disabled = !on;
  }

  // ---- A condition row follows its field ------------------------------------------------------
  function adjustCondition(row) {
    var field = $('[data-role="field"]', row).value;
    var operator = $('[data-role="operator"]', row);
    var text = $('[data-role="value-text"]', row);
    var bool = $('[data-role="value-bool"]', row);
    var isBool = field === "HasAttachment";

    show($('[data-role="header"]', row), field === "Header");
    show(bool, isBool);
    show(text, !isBool);
    text.type = field === "Size" ? "number" : "text";
    var unit = $('[data-role="unit"]', row);
    if (unit) { unit.hidden = field !== "Size"; }

    // A yes/no needs no operator; the others offer the ones that fit the field.
    show(operator, !isBool);
    var allowed = operators[field] || [];
    var current = operator.value;
    var same = allowed.length === operator.options.length && allowed.every(function (o, i) { return operator.options[i].value === o[0]; });
    if (!same && allowed.length) {
      operator.innerHTML = "";
      allowed.forEach(function (o) {
        var option = doc.createElement("option");
        option.value = o[0];
        option.textContent = o[1];
        operator.appendChild(option);
      });
      var keep = allowed.some(function (o) { return o[0] === current; });
      operator.value = keep ? current : allowed[0][0];
    }
  }

  // ---- An action row follows its type ----------------------------------------------------------
  function adjustAction(row) {
    var type = $('[data-role="action-type"]', row).value;
    var value = $('[data-role="action-value"]', row);
    show($('[data-role="folder"]', row), type === "MoveToFolder");
    var needsText = type === "ForwardTo" || type === "AddLabel";
    show(value, needsText);
    if (needsText) {
      value.placeholder = value.getAttribute(type === "ForwardTo" ? "data-placeholder-forward" : "data-placeholder-label") || "";
      value.type = type === "ForwardTo" ? "email" : "text";
    }
  }

  // ---- Numbering and add / remove ---------------------------------------------------------------
  function renumber(list) {
    $$("[data-rule-row]", list).forEach(function (row, index) {
      $$("[name]", row).forEach(function (control) { control.name = control.name.replace(/\[(\d+|__index__)\]/, "[" + index + "]"); });
    });
    var rows = $$("[data-rule-row]", list);
    rows.forEach(function (row) { $("[data-rule-remove]", row).disabled = rows.length === 1; });   // a rule needs a row of each kind
  }

  function add(kind) {
    var template = doc.getElementById("rule-template-" + kind);
    var list = $('[data-rule-list="' + kind + '"]');
    if (!template || !list) { return; }
    var holder = doc.createElement("div");
    holder.innerHTML = template.innerHTML.replace(/__index__/g, String($$("[data-rule-row]", list).length));
    var row = holder.firstElementChild;
    list.appendChild(row);
    if (kind === "condition") { adjustCondition(row); } else { adjustAction(row); }
    renumber(list);
    var first = $("select, input", row);
    if (first) { first.focus(); }
  }

  form.addEventListener("click", function (e) {
    var adder = e.target.closest("[data-rule-add]");
    if (adder) { add(adder.getAttribute("data-rule-add")); return; }
    var remover = e.target.closest("[data-rule-remove]");
    if (remover) {
      var row = remover.closest("[data-rule-row]"), list = row.parentElement;
      if ($$("[data-rule-row]", list).length > 1) { row.remove(); renumber(list); }
    }
  });

  form.addEventListener("change", function (e) {
    var row = e.target.closest("[data-rule-row]");
    if (!row) { return; }
    if (e.target.matches('[data-role="field"]')) { adjustCondition(row); }
    if (e.target.matches('[data-role="action-type"]')) { adjustAction(row); }
  });

  // The server drew the rows, the script only has to make them consistent (and number them).
  $$('[data-rule-row="condition"]').forEach(adjustCondition);
  $$('[data-rule-row="action"]').forEach(adjustAction);
  $$("[data-rule-list]").forEach(renumber);
})();
