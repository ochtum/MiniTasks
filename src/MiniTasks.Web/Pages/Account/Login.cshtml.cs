using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using MiniTasks.Web.Authentication;

namespace MiniTasks.Web.Pages.Account;

[EnableRateLimiting("auth-login")]
public sealed class LoginModel(SupabaseAuthClient auth, AppSessionStore sessions) : PageModel
{
    [BindProperty] public LoginInput Input { get; set; } = new();
    [BindProperty] public string? ReturnUrl { get; set; }
    public bool SessionExpired { get; private set; }

    public void OnGet(string? returnUrl = null, bool expired = false)
    {
        ReturnUrl = SafeReturnUrl(returnUrl);
        SessionExpired = expired;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        SupabaseAuthSession? providerSession;
        try
        {
            providerSession = await auth.SignInAsync(Input.Email.Trim(), Input.Password, HttpContext.RequestAborted);
        }
        catch (AuthenticationUnavailableException error)
        {
            ModelState.AddModelError(string.Empty, error.Message);
            return Page();
        }
        if (providerSession is null)
        {
            ModelState.AddModelError(string.Empty, "メールアドレスまたはパスワードを確認してください。");
            return Page();
        }

        var created = sessions.Create(providerSession);
        try
        {
            if (MiniTasksAuthentication.TryGetSession(User, out var previousId, out var previousUserId))
                await sessions.RevokeAsync(previousId, previousUserId, HttpContext.RequestAborted);

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, created.UserId.ToString("D")),
                new Claim(ClaimTypes.Name, created.Email),
                new Claim(MiniTasksAuthentication.SessionIdClaim, created.SessionId),
            ], MiniTasksAuthentication.Scheme);
            await HttpContext.SignInAsync(MiniTasksAuthentication.Scheme,
                new ClaimsPrincipal(identity), new AuthenticationProperties
                {
                    ExpiresUtc = created.ExpiresAt,
                    IsPersistent = false,
                    AllowRefresh = false,
                });
        }
        catch
        {
            await sessions.RevokeAsync(created.SessionId, created.UserId, CancellationToken.None);
            throw;
        }
        return LocalRedirect(SafeReturnUrl(ReturnUrl));
    }

    private string SafeReturnUrl(string? value) => Url.IsLocalUrl(value) ? value! : "/";

    public sealed class LoginInput
    {
        [Required(ErrorMessage = "メールアドレスを入力してください。")]
        [EmailAddress(ErrorMessage = "メールアドレスの形式を確認してください。")]
        [StringLength(254)]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "パスワードを入力してください。")]
        [StringLength(1024)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";
    }
}
