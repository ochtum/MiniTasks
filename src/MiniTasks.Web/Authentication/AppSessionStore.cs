using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace MiniTasks.Web.Authentication;

public sealed class AppSessionStore(IMemoryCache cache, SupabaseAuthClient auth, TimeProvider clock)
{
    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(1);

    public CreatedAppSession Create(SupabaseAuthSession tokens)
    {
        var sessionId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expiresAt = clock.GetUtcNow() + MiniTasksAuthentication.SessionLifetime;
        var entry = new SessionEntry(tokens, expiresAt);
        cache.Set(Key(sessionId), entry, expiresAt);
        return new CreatedAppSession(sessionId, tokens.UserId, tokens.Email, expiresAt);
    }

    public bool IsActive(string sessionId, Guid userId) =>
        TryGet(sessionId, userId, out _);

    public async Task<string> GetAccessTokenAsync(
        string sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!TryGet(sessionId, userId, out var entry)) throw new SessionExpiredException();
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            EnsureActive(sessionId, entry, userId);
            if (entry.Tokens.AccessExpiresAt <= clock.GetUtcNow() + RefreshBeforeExpiry)
            {
                SupabaseAuthSession refreshed;
                try
                {
                    refreshed = await auth.RefreshAsync(entry.Tokens.RefreshToken, cancellationToken);
                }
                catch (SessionExpiredException)
                {
                    RevokeLocal(sessionId, entry);
                    throw;
                }
                if (refreshed.UserId != userId)
                {
                    RevokeLocal(sessionId, entry);
                    throw new SessionExpiredException();
                }
                // Retain a rotated token for a logout already waiting on this gate. A revoked
                // entry stays out of the cache and cannot return the token to its caller.
                entry.Tokens = refreshed;
                EnsureActive(sessionId, entry, userId);
            }
            return entry.Tokens.AccessToken;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<bool> RevokeAsync(
        string sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!TryGet(sessionId, userId, out var entry)) return true;
        // Local access must end even when an aborted HTTP request cannot acquire the gate.
        // Only the caller that first marks this entry revoked performs provider cleanup.
        if (!RevokeLocal(sessionId, entry)) return true;
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            var tokens = entry.Tokens;
            if (tokens.AccessExpiresAt <= clock.GetUtcNow())
            {
                try
                {
                    // Rotate only to revoke the provider session. Never publish these tokens back
                    // into the removed local entry, even when a refresh was already queued.
                    tokens = await auth.RefreshAsync(tokens.RefreshToken, cancellationToken);
                }
                catch (SessionExpiredException)
                {
                    return true;
                }
                catch (AuthenticationUnavailableException)
                {
                    return false;
                }
                if (tokens.UserId != userId) return false;
            }
            return await auth.SignOutAsync(tokens.AccessToken, cancellationToken);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private bool TryGet(string sessionId, Guid userId, out SessionEntry entry)
    {
        if (cache.TryGetValue(Key(sessionId), out SessionEntry? found) && found is not null &&
            !found.Revoked && found.Tokens.UserId == userId && found.ExpiresAt > clock.GetUtcNow())
        {
            entry = found;
            return true;
        }
        entry = null!;
        return false;
    }

    private void EnsureActive(string sessionId, SessionEntry entry, Guid userId)
    {
        if (entry.Revoked || entry.Tokens.UserId != userId || entry.ExpiresAt <= clock.GetUtcNow())
        {
            RevokeLocal(sessionId, entry);
            throw new SessionExpiredException();
        }
    }

    private bool RevokeLocal(string sessionId, SessionEntry entry)
    {
        var firstRevocation = entry.TryRevoke();
        cache.Remove(Key(sessionId));
        return firstRevocation;
    }

    private static (string, string) Key(string sessionId) => ("MiniTasks.Auth", sessionId);

    private sealed class SessionEntry(SupabaseAuthSession tokens, DateTimeOffset expiresAt)
    {
        private int revoked;
        public SupabaseAuthSession Tokens { get; set; } = tokens;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Revoked => Volatile.Read(ref revoked) != 0;
        public bool TryRevoke() => Interlocked.Exchange(ref revoked, 1) == 0;
    }
}
