using MatMail.Data;
using MatMail.Push;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

/// <summary>MatMail as an app on a phone or a computer: installing it, and the notifications for new mail on the devices of the user.</summary>
public class AppModel(PushService push, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    public bool PushEnabled => push.Enabled;
    public bool CanUseMail => currentUser.Can(Permissions.MailUse);
    public IReadOnlyList<PushSubscription> Devices { get; private set; } = [];

    /// <summary>The texts the page script shows (handed over as JSON, like in the mail client).</summary>
    public IReadOnlyDictionary<string, string> Texts => new Dictionary<string, string>
    {
        ["installed"] = l["You are using the app."],
        ["installHint"] = l["Open the menu of your browser and choose “Install app” or “Add to home screen”."],
        ["installIos"] = l["Tap the share button of Safari, then “Add to Home Screen”. Open MatMail from there afterwards."],
        ["notSupported"] = l["Notifications need a secure connection (https) and a browser that supports them."],
        ["needsApp"] = l["On an iPhone or iPad, first put MatMail on the home screen (share button, “Add to Home Screen”), open it from there and turn notifications on."],
        ["serverOff"] = l["Notifications are switched off on this server."],
        ["blocked"] = l["Your browser blocks notifications for this site. Allow them in the settings of the site, then come back."],
        ["on"] = l["Notifications are on for this device."],
        ["off"] = l["Notifications are off for this device."],
        ["failed"] = l["Notifications could not be turned on."],
        ["testSent"] = l["A test notification was sent."],
        ["testFailed"] = l["The test notification could not be sent. Turn notifications off and on again."],
        ["working"] = l["One moment …"],
    };

    public async Task OnGetAsync(CancellationToken cancel)
        => Devices = await push.ListAsync(currentUser.UserId ?? 0, cancel);

    public async Task<IActionResult> OnPostRemoveAsync(long id, CancellationToken cancel)
    {
        if (await push.RemoveAsync(currentUser.UserId ?? 0, id, cancel))
        {
            this.Notify(l["The device gets no more notifications."].Value);
        }

        return RedirectToPage();
    }
}
