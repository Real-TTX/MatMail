using System.Globalization;
using MatMail.Data;
using MatMail.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace MatMail.Platform;

/// <summary>
/// MatMail's side of the sign-in through MatIdentity (MatPlatform <see cref="IMatIdentityUsers"/>): the person becomes a local user in
/// <see cref="ExternalSignInService"/> (by <c>sub</c>, else a new one; the claim <c>role</c> when it is there) and gets MatMail's own session, with
/// the ID token kept for the sign-out. When an account here has the person's address it is not entered: MatPlatform keeps a pending link and
/// sends the browser to the local form (<c>?local=1&amp;link=1</c>); the account is linked after its own sign-in (<see cref="LinkPendingAsync"/>). A user with two-factor authentication still enters the code:
/// MatMail's rule is that no session comes into being without it, so a ticket is issued and the browser goes to /Account/TwoFactor
/// (<see cref="SendToSecondStep"/>). Mail programs are not affected: IMAP and SMTP keep using the user's password or app passwords.
/// </summary>
public sealed class MailIdentityUsers(ExternalSignInService external, SignInService signIn, TwoFactorPolicy twoFactor, TwoFactorTicket ticket) : IMatIdentityUsers
{
    /// <summary>Set in <see cref="HttpContext.Items"/> when the code is still to come.</summary>
    public const string SecondStepItem = "matmail:matidentity-second-step";

    /// <summary>The return address of the sign-in at MatIdentity (MatPlatform does not hand it to <see cref="IMatIdentityUsers"/>).</summary>
    public const string ReturnUrlItem = "matmail:matidentity-return-url";

    public async Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct)
    {
        ExternalSignInResult result = await external.SignInAsync(
            new ExternalPerson(person.Subject, person.Email, person.Name, person.PreferredUserName, person.Role),
            http.ClientAddress(),
            ct);
        if (result.LinkTo is { } match)
        {
            // An account here has the person's address: it is linked only after its own sign-in (LinkPendingAsync), never by the address.
            return MatIdentitySignInResult.LinkRequired(AccountKey(match.Id));
        }

        if (!result.Succeeded)
        {
            return MatIdentitySignInResult.Refused(result.Error ?? "refused");
        }

        var user = result.User!;
        if ((await twoFactor.GetStatusAsync(user, ct)).Enabled)
        {
            // An empty token still marks the ticket as one that comes from MatIdentity (it has to be Lax to survive the redirect chain).
            string? returnUrl = http.Items[ReturnUrlItem] as string;
            ticket.Issue(http, new PendingSignIn(user.Id, true, returnUrl, null, person.IdToken ?? string.Empty));
            http.Items[SecondStepItem] = true;
            return MatIdentitySignInResult.Success();
        }

        await signIn.SignInAsync(user, true, person.IdToken);
        await signIn.CompleteSignInAsync(user.Id, DateTime.UtcNow);
        return MatIdentitySignInResult.Success();
    }

    /// <summary>
    /// After a local sign-in that is complete (the password, and the code when the user has a second factor), right before the session is made:
    /// links the user with the person of MatIdentity whose link is pending in this browser (<see cref="MatIdentityPendingLink"/>), when it was made
    /// for exactly this user; another user links nothing, and the pending link is gone either way. Returns the ID token to keep with the session
    /// (null: nothing was linked).
    /// </summary>
    public async Task<string?> LinkPendingAsync(HttpContext http, User user, CancellationToken ct)
    {
        MatIdentityPendingLink? link = MatIdentityPendingLink.TakeFor(http, AccountKey(user.Id));
        if (link is null)
        {
            return null;
        }

        ExternalSignInResult linked = await external.LinkAsync(
            user.Id,
            new ExternalPerson(link.Subject, link.Email, link.Name, link.PreferredUserName, link.Role),
            http.ClientAddress(),
            ct);
        return linked.Succeeded ? link.IdToken : null;
    }

    /// <summary>The key of a user in a <see cref="MatIdentityPendingLink"/>.</summary>
    public static string AccountKey(long userId) => userId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Wraps MatPlatform's handling of the answer of MatIdentity: hands the return address over to <see cref="SignInAsync"/> and, when the code is
    /// still to come, sends the browser to /Account/TwoFactor instead of the return address (a user bound to set it up is held on
    /// /Account/Security by <see cref="TwoFactorSetupMiddleware"/> anyway).
    /// </summary>
    public static void SendToSecondStep(OpenIdConnectOptions options)
    {
        Func<Microsoft.AspNetCore.Authentication.TicketReceivedContext, Task> signInLocally = options.Events.OnTicketReceived;
        options.Events.OnTicketReceived = async context =>
        {
            context.HttpContext.Items[ReturnUrlItem] = context.ReturnUri;
            await signInLocally(context);
            if (context.HttpContext.Items.ContainsKey(SecondStepItem))
            {
                context.Response.Redirect("/Account/TwoFactor");
            }
        };
    }
}
