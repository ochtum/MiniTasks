using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using MiniTasks.Web.Authentication;

namespace MiniTasks.Web.Pages.Account;

[EnableRateLimiting("auth-logout")]
public sealed class LogoutModel(AppSessionStore sessions, ILogger<LogoutModel> logger) : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            if (MiniTasksAuthentication.TryGetSession(User, out var id, out var userId) &&
                !await sessions.RevokeAsync(id, userId, HttpContext.RequestAborted))
                logger.LogWarning("Supabase session revocation could not be confirmed; local session was removed.");
        }
        finally
        {
            await HttpContext.SignOutAsync(MiniTasksAuthentication.Scheme);
        }
        return LocalRedirect("/account/login");
    }
}
