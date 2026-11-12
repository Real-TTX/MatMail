// MatMail web client – the colours of a mail in the dark theme.
// A mail (or a signature in the compose window) that sets its own text colours assumes a light page: dark grey or plum text on the
// dark theme cannot be read. What is too dark for the background it stands on is made lighter here – the hue stays, only the
// lightness moves until the contrast is enough (as Outlook and Apple Mail do). Only what is shown changes: the style attribute the
// sender wrote is kept in data-mm-style and put back (restore, clean), so a reply or a sent message carries the original colours.
(function () {
  "use strict";

  var App = window.MailApp = window.MailApp || {};
  var MARK = "data-mm-style";
  var NONE = "\u0001";            // the element had no style attribute
  var LIMIT = 8000;               // elements looked at in one go (a huge newsletter is not worth the time)
  var TEXT = 4.5;                 // contrast wanted for text (WCAG AA)
  var LINE = 3;                   // and for borders

  App.colours = {
    adapt: adapt, restore: restore, clean: clean, neutralise: neutralise, paintedBackground: paintedBackground,
    backgroundOf: backgroundOf, darkTheme: darkTheme, onThemeChange: onThemeChange
  };

  // ---------------------------------------------------------------------------------------------
  // The theme of the page
  // ---------------------------------------------------------------------------------------------

  // The attribute data-theme of <html> says "light" or "dark"; without it (or "system") the device decides.
  var systemDark = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;
  var listeners = [];

  function darkTheme() {
    var theme = document.documentElement.getAttribute("data-theme");
    return theme === "dark" || (theme !== "light" && !!systemDark && systemDark.matches);
  }

  function onThemeChange(listener) { listeners.push(listener); }
  function themeChanged() { listeners.forEach(function (listener) { listener(); }); }
  if (window.MutationObserver) {
    new MutationObserver(themeChanged).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
  }
  if (systemDark && systemDark.addEventListener) { systemDark.addEventListener("change", themeChanged); }

  // ---------------------------------------------------------------------------------------------
  // Colour arithmetic
  // ---------------------------------------------------------------------------------------------

  /** "rgb(1, 2, 3)" / "rgba(1, 2, 3, 0.5)" / "rgb(1 2 3 / 50%)" → {r, g, b, a}; anything else → null. */
  function parse(value) {
    var m = /rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:\s*[,/]\s*([\d.]+)(%?))?\s*\)/.exec(value || "");
    if (!m) { return null; }
    var alpha = m[4] === undefined ? 1 : parseFloat(m[4]) / (m[5] ? 100 : 1);
    return { r: +m[1], g: +m[2], b: +m[3], a: alpha };
  }

  function channel(v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }
  function luminance(c) { return 0.2126 * channel(c.r) + 0.7152 * channel(c.g) + 0.0722 * channel(c.b); }
  function contrast(a, b) {
    var x = luminance(a), y = luminance(b);
    return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
  }
  function isDarkColour(c) { return !!c && luminance(c) < 0.2; }

  function toHsl(c) {
    var r = c.r / 255, g = c.g / 255, b = c.b / 255;
    var max = Math.max(r, g, b), min = Math.min(r, g, b), l = (max + min) / 2, h = 0, s = 0;
    if (max !== min) {
      var d = max - min;
      s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
      h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
      h /= 6;
    }
    return { h: h, s: s, l: l };
  }

  function fromHsl(h, s, l) {
    function part(p, q, t) {
      if (t < 0) { t += 1; } if (t > 1) { t -= 1; }
      if (t < 1 / 6) { return p + (q - p) * 6 * t; }
      if (t < 1 / 2) { return q; }
      if (t < 2 / 3) { return p + (q - p) * (2 / 3 - t) * 6; }
      return p;
    }
    if (s === 0) { var v = Math.round(l * 255); return { r: v, g: v, b: v, a: 1 }; }
    var q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
    return { r: Math.round(part(p, q, h + 1 / 3) * 255), g: Math.round(part(p, q, h) * 255), b: Math.round(part(p, q, h - 1 / 3) * 255), a: 1 };
  }

  /** The colour with the same hue, light enough to stand on the (dark) background with the contrast wanted. */
  function lighten(c, background, wanted) {
    var hsl = toHsl(c);
    var result = c;
    for (var l = Math.max(hsl.l, 0.35); l <= 1.001; l += 0.025) {
      result = fromHsl(hsl.h, Math.min(hsl.s, 0.85), Math.min(l, 1));
      if (contrast(result, background) >= wanted) { break; }
    }
    return "rgb(" + result.r + ", " + result.g + ", " + result.b + ")";
  }

  // ---------------------------------------------------------------------------------------------
  // The document
  // ---------------------------------------------------------------------------------------------

  function styleOf(element) { return element.ownerDocument.defaultView.getComputedStyle(element); }

  /** The first colour that is really painted behind a node (walking up), or null when nothing is. */
  function backgroundOf(node, stop) {
    for (var el = node; el && el.nodeType === 1; el = el.parentElement) {
      var c = parse(styleOf(el).backgroundColor);
      if (c && c.a >= 0.5) { return c; }
      if (el === stop) { break; }
    }
    return null;
  }

  function hasOwnText(element) {
    for (var n = element.firstChild; n; n = n.nextSibling) {
      if (n.nodeType === 3 && n.nodeValue.trim()) { return true; }
    }
    return false;
  }

  function remember(element) {
    if (!element.hasAttribute(MARK)) { element.setAttribute(MARK, element.hasAttribute("style") ? element.getAttribute("style") : NONE); }
  }

  function elementsOf(root) { return Array.prototype.slice.call(root.querySelectorAll("*"), 0, LIMIT); }

  /**
   * Makes what is too dark to read on the page's dark background (pageBackground, {r, g, b}) lighter: the colour of text and of borders.
   * Text that stands on a light box of its own (a button of a newsletter, a highlighted cell) keeps its colours.
   */
  function adapt(root, pageBackground) {
    var changes = [];
    elementsOf(root).forEach(function (element) {
      var cs = styleOf(element);
      var background = backgroundOf(element, root) || pageBackground;
      if (!isDarkColour(background)) { return; }
      var change = [];
      var colour = parse(cs.color);
      if (colour && hasOwnText(element) && contrast(colour, background) < TEXT) { change.push(["color", lighten(colour, background, TEXT)]); }
      ["top", "right", "bottom", "left"].forEach(function (side) {
        if (cs["border-" + side + "-style"] === "none" || parseFloat(cs["border-" + side + "-width"]) <= 0) { return; }
        var line = parse(cs["border-" + side + "-color"]);
        if (line && line.a > 0 && contrast(line, background) < LINE) { change.push(["border-" + side + "-color", lighten(line, background, LINE)]); }
      });
      if (change.length) { changes.push([element, change]); }
    });

    // Written after all are measured: a change would otherwise make the browser lay out the page again for every element.
    changes.forEach(function (entry) {
      remember(entry[0]);
      entry[1].forEach(function (c) { entry[0].style.setProperty(c[0], c[1], "important"); });
    });
  }

  /** Large areas the sender painted white (a body or a frame table of an Outlook mail) are taken away, so that the dark page shows. */
  function neutralise(root, areas) {
    areas.forEach(function (element) {
      remember(element);
      element.style.setProperty("background-color", "transparent", "important");
    });
    return root;
  }

  /**
   * What the sender painted: a layout with colours of its own (a newsletter, a report with shaded rows) is shown on its light page as it
   * is – darkening only half of it looks broken. Returns {coloured: true} when a picture or a colour other than white lies behind a
   * noticeable part of the mail (a band across it, or more than a small spot); else {coloured: false, white: [elements]}: the white areas
   * behind larger parts (the body or the frame table of an Outlook mail), which the dark theme may take away. Small marks (a highlighted
   * word) count for neither.
   */
  function paintedBackground(root) {
    var width = Math.max(1, root.scrollWidth);
    var area = Math.max(1, width * root.scrollHeight);
    var white = [];
    var coloured = elementsOf(root).some(function (element) {
      var cs = styleOf(element);
      var picture = cs.backgroundImage && cs.backgroundImage !== "none";
      var colour = parse(cs.backgroundColor);
      if (!picture && !(colour && colour.a >= 0.5)) { return false; }
      var box = element.getBoundingClientRect();
      var size = box.width * box.height;
      var noticeable = size >= area * 0.03 || (box.width >= width * 0.5 && box.height >= 20);
      if (!noticeable) { return false; }
      if (!picture && toHsl(colour).l >= 0.98) {
        if (size >= area * 0.2) { white.push(element); return false; }
        return false;
      }
      return true;
    });
    return { coloured: coloured, white: coloured ? [] : white };
  }

  /** Puts back the style attributes the sender wrote (the theme went light again). */
  function restore(root) {
    Array.prototype.forEach.call(root.querySelectorAll("[" + MARK + "]"), function (element) {
      var original = element.getAttribute(MARK);
      if (original === NONE) { element.removeAttribute("style"); } else { element.setAttribute("style", original); }
      element.removeAttribute(MARK);
    });
  }

  /** The HTML of an element as written, without what the dark theme changed (what is sent or saved as a draft). */
  function clean(element) {
    if (!element.querySelector("[" + MARK + "]")) { return element.innerHTML; }
    var copy = element.cloneNode(true);
    restore(copy);
    return copy.innerHTML;
  }
})();
