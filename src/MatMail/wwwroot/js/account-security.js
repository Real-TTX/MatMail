// MatMail – the Security page of the account. After a request the server refused, the page comes back at the top: the dialog for a new app
// password opens again when the error is in its fields, and any other error is brought into view.
(function () {
  "use strict";

  var doc = document;

  function showError() {
    var dialog = doc.getElementById("new-app-password");
    if (dialog && dialog.querySelector(".mat-field.is-invalid") && window.MatUi) { window.MatUi.dialog.open(dialog); return; }
    var invalid = doc.querySelector(".mat-field.is-invalid");
    if (invalid) { invalid.scrollIntoView({ block: "center" }); }
  }

  if (doc.readyState === "loading") { doc.addEventListener("DOMContentLoaded", showError); } else { showError(); }
})();
