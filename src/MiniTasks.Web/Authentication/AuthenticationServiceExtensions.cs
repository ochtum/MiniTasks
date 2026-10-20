using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MiniTasks.Web.Authentication;

public static class AuthenticationServiceExtensions
{
    public static IServiceCollection AddMiniTasksAuthentication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddSingleton<SupabaseAuthClient>();
        services.AddSingleton<AppSessionStore>();
        services.AddScoped<ITokenProvider, SessionTokenProvider>();
        services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
        services.AddCascadingAuthenticationState();
        services.AddAuthorization();
        services.AddRazorPages();
        services.AddAuthentication(MiniTasksAuthentication.Scheme)
            .AddCookie(MiniTasksAuthentication.Scheme, options =>
            {
                options.Cookie.Name = "MiniTasks.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                options.LoginPath = "/account/login";
                options.AccessDeniedPath = "/account/login";
                options.ExpireTimeSpan = MiniTasksAuthentication.SessionLifetime;
                options.SlidingExpiration = false;
                options.Events.OnValidatePrincipal = async context =>
                {
                    var sessions = context.HttpContext.RequestServices.GetRequiredService<AppSessionStore>();
                    if (!MiniTasksAuthentication.TryGetSession(context.Principal, out var id, out var userId) ||
                        !sessions.IsActive(id, userId))
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(MiniTasksAuthentication.Scheme);
                    }
                };
            });
        services.AddOptions<CookieAuthenticationOptions>(MiniTasksAuthentication.Scheme)
            .Configure<IWebHostEnvironment>((options, environment) =>
                options.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always);
        return services;
    }
}
