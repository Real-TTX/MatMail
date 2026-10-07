using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatMail.Pages;

/// <summary>The front door: sends everybody to the place they can use.</summary>
public class IndexModel(CurrentUser currentUser) : PageModel
{
    public IActionResult OnGet()
    {
        if (currentUser.Can(Permissions.MailUse))
        {
            return Redirect("/Mail");
        }

        return Redirect(currentUser.CanAdminister ? "/Admin" : "/Account");
    }
}
