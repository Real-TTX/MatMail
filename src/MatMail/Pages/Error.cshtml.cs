using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatMail.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class ErrorModel : PageModel
{
    [BindProperty(SupportsGet = true, Name = "code")]
    public int Code { get; set; }

    public void OnGet()
    {
        if (Code == 0)
        {
            Code = Response.StatusCode >= 400 ? Response.StatusCode : 500;
        }
    }
}
