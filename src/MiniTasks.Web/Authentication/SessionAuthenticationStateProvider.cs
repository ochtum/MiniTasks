using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace MiniTasks.Web.Authentication;

public sealed class SessionAuthenticationStateProvider(
    ILoggerFactory loggerFactory, AppSessionStore sessions)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        return Task.FromResult(MiniTasksAuthentication.TryGetSession(
            authenticationState.User, out var id, out var userId) && sessions.IsActive(id, userId));
    }
}
