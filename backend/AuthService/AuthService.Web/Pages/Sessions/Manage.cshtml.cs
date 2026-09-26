using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthService.Web.Pages.Sessions;

// The list itself is fetched client-side from GET /auth/sessions - same reasoning as
// Passkeys/Manage: this page is just the authenticated shell.
[Authorize(AuthenticationSchemes = "Identity.Application")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ManageModel : PageModel
{
    public void OnGet()
    {
    }
}
