// MatMail – the page of a directory (Administration → Directories → edit): the port follows the encryption, and "Usual values" fills in the filter,
// the login attribute and the attributes of the common servers. What depends on other fields (the group switches) is done by the toolkit (data-show-if).
(function () {
  "use strict";

  var doc = document;
  function field(name) { return doc.getElementById("Input_" + name); }

  var security = field("Security");
  var port = field("Port");
  var presetField = field("Preset");
  if (!security || !port || !presetField) { return; }

  // The port follows the encryption while it still holds a default.
  var defaults = { None: 389, StartTls: 389, Ldaps: 636 };
  var lastSecurity = security.value;
  security.addEventListener("change", function () {
    var value = parseInt(port.value, 10);
    if (!value || value === defaults[lastSecurity]) { port.value = defaults[security.value]; }
    lastSecurity = security.value;
  });

  // "Usual values": a starting point for the filter and the attributes of the common servers.
  var presets = {
    ad: {
      UserFilter: "(&(objectCategory=person)(objectClass=user))", LoginAttribute: "sAMAccountName", DisplayNameAttribute: "displayName",
      EmailAttribute: "mail", FirstNameAttribute: "givenName", LastNameAttribute: "sn", JobTitleAttribute: "title",
      PhoneAttribute: "telephoneNumber", MobileAttribute: "mobile", DepartmentAttribute: "department", GroupLookup: "UserMemberOf", NestedGroups: true
    },
    openldap: {
      UserFilter: "(objectClass=inetOrgPerson)", LoginAttribute: "uid", DisplayNameAttribute: "cn",
      EmailAttribute: "mail", FirstNameAttribute: "givenName", LastNameAttribute: "sn", JobTitleAttribute: "title",
      PhoneAttribute: "telephoneNumber", MobileAttribute: "mobile", DepartmentAttribute: "departmentNumber", GroupLookup: "GroupMembers", NestedGroups: false
    },
    freeipa: {
      UserFilter: "(objectClass=posixAccount)", LoginAttribute: "uid", DisplayNameAttribute: "displayName",
      EmailAttribute: "mail", FirstNameAttribute: "givenName", LastNameAttribute: "sn", JobTitleAttribute: "title",
      PhoneAttribute: "telephoneNumber", MobileAttribute: "mobile", DepartmentAttribute: "ou", GroupLookup: "UserMemberOf", NestedGroups: false
    }
  };

  presetField.addEventListener("change", function () {
    var preset = presets[presetField.value];
    if (!preset) { return; }
    Object.keys(preset).forEach(function (name) {
      var input = field(name);
      if (!input) { return; }
      if (input.type === "checkbox") { input.checked = !!preset[name]; } else { input.value = preset[name]; }
      // The toolkit shows or hides what depends on the value (the nested groups depend on where the groups are read).
      input.dispatchEvent(new Event("change", { bubbles: true }));
    });
    presetField.value = "";
  });
})();
