// MatMail – the page of a connected account (Administration → Connected accounts → edit).
// What depends on the role (the explanation, the fields only an account that fetches mail has, the sign-in of the SMTP server) is shown and hidden
// by the toolkit (data-show-if in the markup). What is left here: the default ports follow the protocol and the encryption, and an account that
// only sends has no receiving credentials to share, so it asks for its own.
(function () {
  "use strict";

  var doc = document;
  function $(id) { return doc.getElementById(id); }

  var role = $("Input_Role");
  var protocol = $("Input_ReceiveProtocol");
  var security = $("Input_ReceiveSecurity");
  var port = $("Input_ReceivePort");
  var sendSecurity = $("Input_SendSecurity");
  var sendPort = $("Input_SendPort");
  var same = $("Input_SendUsesReceiveCredentials");
  if (!role || !protocol || !security || !port || !sendSecurity || !sendPort || !same) { return; }

  // The port follows protocol and encryption as long as it still holds one of the default values (a port somebody typed stays).
  var receiveDefaults = { "Imap:Ssl": 993, "Imap:StartTls": 143, "Imap:None": 143, "Pop3:Ssl": 995, "Pop3:StartTls": 110, "Pop3:None": 110 };
  var sendDefaults = { Ssl: 465, StartTls: 587, None: 25 };

  function holdsDefault(input, defaults) {
    var value = parseInt(input.value, 10);
    return !value || Object.keys(defaults).some(function (key) { return defaults[key] === value; });
  }

  function receivePorts() {
    if (holdsDefault(port, receiveDefaults)) { port.value = receiveDefaults[protocol.value + ":" + security.value]; }
  }

  function sendPorts() {
    if (holdsDefault(sendPort, sendDefaults)) { sendPort.value = sendDefaults[sendSecurity.value]; }
  }

  protocol.addEventListener("change", receivePorts);
  security.addEventListener("change", receivePorts);
  sendSecurity.addEventListener("change", sendPorts);

  // The toolkit looks at the check after this (its listener is on the document), so the sign-in fields appear in the same step.
  role.addEventListener("change", function () {
    if (role.value === "SendOnly") { same.checked = false; }
  });
})();
