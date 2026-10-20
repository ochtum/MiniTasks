using System.Security.Claims;

namespace MiniTasks.Web.Authentication;

public interface ITokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class SessionExpiredException : Exception
{
    public SessionExpiredException() : base("ログインの有効期限が切れました。再度ログインしてください。") { }
}

public sealed class AuthenticationUnavailableException : Exception
{
    public AuthenticationUnavailableException()
        : base("認証サービスを利用できません。時間をおいて再度お試しください。") { }
}

public static class MiniTasksAuthentication
{
    public const string Scheme = "MiniTasks.Cookie";
    public const string SessionIdClaim = "minitasks_session_id";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    public static bool TryGetSession(ClaimsPrincipal? principal, out string sessionId, out Guid userId)
    {
        sessionId = principal?.FindFirst(SessionIdClaim)?.Value ?? "";
        userId = default;
        return principal?.Identity?.IsAuthenticated == true && !string.IsNullOrEmpty(sessionId) &&
            Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
    }
}

public sealed record SupabaseAuthSession(
    Guid UserId, string Email, string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt);

public sealed record CreatedAppSession(
    string SessionId, Guid UserId, string Email, DateTimeOffset ExpiresAt);
