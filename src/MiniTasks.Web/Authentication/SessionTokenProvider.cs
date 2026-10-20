using Microsoft.AspNetCore.Components.Authorization;

namespace MiniTasks.Web.Authentication;

public sealed class SessionTokenProvider(
    AuthenticationStateProvider authenticationState, AppSessionStore sessions) : ITokenProvider
{
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var state = await authenticationState.GetAuthenticationStateAsync();
        if (!MiniTasksAuthentication.TryGetSession(state.User, out var id, out var userId))
            throw new SessionExpiredException();
        return await sessions.GetAccessTokenAsync(id, userId, cancellationToken);
    }
}
