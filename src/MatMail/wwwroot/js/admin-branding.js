// MatMail – the page Branding (Administration → Branding): the preview follows the accent colour that is chosen.
// The preview is an element with its own data-app, so the toolkit derives its colours (the button, the logo tile) from the two properties set here.
(function () {
  "use strict";

  var doc = document;
  var color = doc.getElementById("Input_AccentColor");
  var use = doc.getElementById("Input_UseAccent");
  var preview = doc.getElementById("brand-preview");
  if (!color || !use || !preview) { return; }

  // White or near-black, whichever reads better on the colour: the rule of the server (BrandingService.TextOn), so the preview shows what the pages get.
  function textOn(hex) {
    function linear(offset) {
      var c = parseInt(hex.substr(offset, 2), 16) / 255;
      return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
    }
    var luminance = 0.2126 * linear(1) + 0.7152 * linear(3) + 0.0722 * linear(5);
    return 1.05 / (luminance + 0.05) >= (luminance + 0.05) / 0.0632 ? "#ffffff" : "#1b2130";
  }

  function update() {
    if (use.checked && /^#[0-9a-f]{6}$/i.test(color.value)) {
      preview.style.setProperty("--mat-app-accent", color.value);
      preview.style.setProperty("--mat-app-on-accent", textOn(color.value));
    } else {
      // No colour of the tenant: the colour of the app, with white text on it.
      preview.style.removeProperty("--mat-app-accent");
      preview.style.setProperty("--mat-app-on-accent", "initial");
    }
    preview.style.setProperty("--mat-app-accent-text", "initial");
  }

  // The colour picker of the toolkit writes the colour into its input and fires input and change.
  color.addEventListener("input", update);
  color.addEventListener("change", update);
  use.addEventListener("change", update);
  update();
})();
