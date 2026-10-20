using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiniTasks.Web.Authentication;
using MiniTasks.Web.Infrastructure;
using MiniTasks.Web.Pages.Account;

namespace MiniTasks.Tests.Authentication;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task PasswordGrantUsesApplicationKeyAndReturnsProviderIdentity()
    {
        var userId = Guid.NewGuid();
        using var fixture = new Fixture(async (request, _) =>
        {
            Assert.Equal("/auth/v1/token?grant_type=password", request.RequestUri!.PathAndQuery);
            Assert.Equal("synthetic-publishable", request.Headers.GetValues("apikey").Single());
            Assert.Null(request.Headers.Authorization);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("a@example.test", body.GetProperty("email").GetString());
            Assert.Equal("synthetic-password", body.GetProperty("password").GetString());
            return TokenResponse(userId, "access-a", "refresh-a");
        });
        var result = await fixture.Auth.SignInAsync("a@example.test", "synthetic-password");
        Assert.Equal(userId, result!.UserId);
        Assert.Equal("access-a", result.AccessToken);
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(1), result.AccessExpiresAt);
    }

    [Fact]
    public async Task InvalidCredentialsAndUnavailableProviderAreDifferentFailures()
    {
        using var rejected = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        Assert.Null(await rejected.Auth.SignInAsync("a@example.test", "wrong"));
        await Assert.ThrowsAsync<SessionExpiredException>(() => rejected.Auth.RefreshAsync("old-refresh"));

        using var unavailable = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("sensitive-provider-content"),
        }));
        var error = await Assert.ThrowsAsync<AuthenticationUnavailableException>(
            () => unavailable.Auth.SignInAsync("a@example.test", "secret"));
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task SeparateLoginsOfSameAndDifferentUsersKeepDistinctTokens()
    {
        using var fixture = new Fixture();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var first = fixture.Create(userA, "a-one");
        var second = fixture.Create(userA, "a-two");
        var third = fixture.Create(userB, "b-one");
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal("a-one", await fixture.Store.GetAccessTokenAsync(first.SessionId, userA));
        Assert.Equal("a-two", await fixture.Store.GetAccessTokenAsync(second.SessionId, userA));
        Assert.Equal("b-one", await fixture.Store.GetAccessTokenAsync(third.SessionId, userB));
        await Assert.ThrowsAsync<SessionExpiredException>(
            () => fixture.Store.GetAccessTokenAsync(first.SessionId, userB));
        Assert.True(fixture.Store.IsActive(first.SessionId, userA));
    }

    [Fact]
    public async Task ConcurrentCallsRefreshOnceAndUseRotatedToken()
    {
        var userId = Guid.NewGuid();
        var calls = 0;
        using var fixture = new Fixture(async (request, cancellationToken) =>
        {
            Assert.Contains("grant_type=refresh_token", request.RequestUri!.Query);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal("refresh-old", body.GetProperty("refresh_token").GetString());
            Interlocked.Increment(ref calls);
            await Task.Delay(15, cancellationToken);
            return TokenResponse(userId, "access-new", "refresh-new");
        });
        var session = fixture.Create(userId, "access-old", expired: true);
        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => fixture.Store.GetAccessTokenAsync(session.SessionId, userId)));
        Assert.Equal(1, calls);
        Assert.All(results, token => Assert.Equal("access-new", token));
    }

    [Fact]
    public async Task LogoutImmediatelyRevokesLocalAccessAndWaitsToRevokeRotatedProviderTokenOnce()
    {
        var userId = Guid.NewGuid();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? logoutToken = null;
        var logoutCalls = 0;
        using var fixture = new Fixture(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
            {
                refreshStarted.SetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return TokenResponse(userId, "access-new", "refresh-new");
            }
            Assert.Equal("/auth/v1/logout?scope=local", request.RequestUri.PathAndQuery);
            logoutToken = request.Headers.Authorization?.Parameter;
            Interlocked.Increment(ref logoutCalls);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var session = fixture.Create(userId, "access-old", expired: true);
        var refresh = fixture.Store.GetAccessTokenAsync(session.SessionId, userId);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var logout = fixture.Store.RevokeAsync(session.SessionId, userId);
        Assert.False(logout.IsCompleted);
        Assert.False(fixture.Store.IsActive(session.SessionId, userId));
        Assert.True(await fixture.Store.RevokeAsync(session.SessionId, userId));
        releaseRefresh.SetResult();
        await Assert.ThrowsAsync<SessionExpiredException>(() => refresh);
        Assert.True(await logout);
        Assert.Equal("access-new", logoutToken);
        Assert.Equal(1, logoutCalls);
        Assert.False(fixture.Store.IsActive(session.SessionId, userId));
        await Assert.ThrowsAsync<SessionExpiredException>(
            () => fixture.Store.GetAccessTokenAsync(session.SessionId, userId));
    }

    [Fact]
    public async Task CanceledLogoutWaitingForRefreshStillInvalidatesLocalSession()
    {
        var userId = Guid.NewGuid();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logoutCalls = 0;
        using var fixture = new Fixture(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
            {
                refreshStarted.SetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return TokenResponse(userId, "access-new", "refresh-new");
            }
            Interlocked.Increment(ref logoutCalls);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var session = fixture.Create(userId, "access-old", expired: true);
        var otherBrowser = fixture.Create(userId, "other-browser");
        var refresh = fixture.Store.GetAccessTokenAsync(session.SessionId, userId);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var canceled = new CancellationTokenSource();
        var logout = fixture.Store.RevokeAsync(session.SessionId, userId, canceled.Token);
        Assert.False(logout.IsCompleted);
        Assert.False(fixture.Store.IsActive(session.SessionId, userId));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => logout);
        await Assert.ThrowsAsync<SessionExpiredException>(
            () => fixture.Store.GetAccessTokenAsync(session.SessionId, userId));
        releaseRefresh.SetResult();
        await Assert.ThrowsAsync<SessionExpiredException>(() => refresh);
        Assert.Equal(0, logoutCalls);
        Assert.Equal("other-browser", await fixture.Store.GetAccessTokenAsync(otherBrowser.SessionId, userId));
    }

    [Fact]
    public async Task LogoutRefreshesExpiredProviderTokenWithoutRestoringLocalAccess()
    {
        var userId = Guid.NewGuid();
        var calls = new List<string>();
        AppSessionStore? store = null;
        CreatedAppSession? session = null;
        using var fixture = new Fixture((request, _) =>
        {
            calls.Add(request.RequestUri!.PathAndQuery);
            Assert.False(store!.IsActive(session!.SessionId, userId));
            if (request.RequestUri.AbsolutePath.EndsWith("/token"))
                return Task.FromResult(TokenResponse(userId, "logout-access", "logout-refresh"));
            Assert.Equal("logout-access", request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        store = fixture.Store;
        session = fixture.Create(userId, "expired-access", expired: true);
        Assert.True(await store.RevokeAsync(session.SessionId, userId));
        Assert.Equal(new[] { "/auth/v1/token?grant_type=refresh_token", "/auth/v1/logout?scope=local" }, calls);
        await Assert.ThrowsAsync<SessionExpiredException>(() => store.GetAccessTokenAsync(session.SessionId, userId));
    }

    [Fact]
    public async Task FailedProviderLogoutStillRemovesOnlyTheLocalLogin()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var userId = Guid.NewGuid();
        var first = fixture.Create(userId, "first");
        var otherBrowser = fixture.Create(userId, "other-browser");
        Assert.False(await fixture.Store.RevokeAsync(first.SessionId, userId));
        Assert.False(fixture.Store.IsActive(first.SessionId, userId));
        Assert.Equal("other-browser", await fixture.Store.GetAccessTokenAsync(otherBrowser.SessionId, userId));
    }

    [Fact]
    public async Task RefreshDoesNotExtendEightHourApplicationSession()
    {
        var userId = Guid.NewGuid();
        using var fixture = new Fixture((_, _) => Task.FromResult(TokenResponse(userId, "refreshed", "rotated")));
        var session = fixture.Create(userId, "initial");
        fixture.Clock.Advance(TimeSpan.FromHours(7.9));
        Assert.Equal("refreshed", await fixture.Store.GetAccessTokenAsync(session.SessionId, userId));
        fixture.Clock.Advance(TimeSpan.FromHours(0.2));
        Assert.False(fixture.Store.IsActive(session.SessionId, userId));
        await Assert.ThrowsAsync<SessionExpiredException>(
            () => fixture.Store.GetAccessTokenAsync(session.SessionId, userId));
    }

    [Fact]
    public async Task InvalidRefreshOrChangedIdentityInvalidatesLocalSession()
    {
        var userId = Guid.NewGuid();
        using var invalid = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var first = invalid.Create(userId, "old", expired: true);
        await Assert.ThrowsAsync<SessionExpiredException>(() => invalid.Store.GetAccessTokenAsync(first.SessionId, userId));
        Assert.False(invalid.Store.IsActive(first.SessionId, userId));

        using var changed = new Fixture((_, _) => Task.FromResult(TokenResponse(Guid.NewGuid(), "other", "other")));
        var second = changed.Create(userId, "old", expired: true);
        await Assert.ThrowsAsync<SessionExpiredException>(() => changed.Store.GetAccessTokenAsync(second.SessionId, userId));
        Assert.False(changed.Store.IsActive(second.SessionId, userId));
    }

    [Fact]
    public async Task ExistingCircuitCannotReadAfterSessionIsRevoked()
    {
        using var fixture = new Fixture();
        var userId = Guid.NewGuid();
        var session = fixture.Create(userId, "access");
        var principal = Principal(session);
        var tokenProvider = new SessionTokenProvider(new FixedAuthenticationState(principal), fixture.Store);
        Assert.Equal("access", await tokenProvider.GetAccessTokenAsync());
        await fixture.Store.RevokeAsync(session.SessionId, userId);
        await Assert.ThrowsAsync<SessionExpiredException>(() => tokenProvider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task NewLoginRevokesPreviousBrowserSessionAndRejectsExternalReturnUrl()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        using var fixture = new Fixture((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/token")
                ? TokenResponse(userB, "access-b", "refresh-b")
                : new HttpResponseMessage(HttpStatusCode.NoContent)));
        var previous = fixture.Create(userA, "access-a");
        var authentication = new CapturingAuthenticationService();
        using var services = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider();
        var context = new DefaultHttpContext { User = Principal(previous), RequestServices = services };
        var actionContext = new ActionContext(context, new RouteData(), new ActionDescriptor());
        var page = new LoginModel(fixture.Auth, fixture.Store)
        {
            PageContext = new PageContext { HttpContext = context },
            Url = new UrlHelper(actionContext),
            ReturnUrl = "https://external.example/",
            Input = new LoginModel.LoginInput { Email = "b@example.test", Password = "synthetic-password" },
        };
        var result = Assert.IsType<LocalRedirectResult>(await page.OnPostAsync());
        Assert.Equal("/", result.Url);
        Assert.False(fixture.Store.IsActive(previous.SessionId, userA));
        Assert.NotNull(authentication.SignedIn);
        Assert.True(MiniTasksAuthentication.TryGetSession(authentication.SignedIn, out var id, out var authenticatedUser));
        Assert.Equal(userB, authenticatedUser);
        Assert.Equal("access-b", await fixture.Store.GetAccessTokenAsync(id, userB));
        Assert.DoesNotContain(authentication.SignedIn!.Claims, claim => claim.Value.Contains("access-b") || claim.Value.Contains("refresh-b"));
        Assert.Equal(previous.ExpiresAt.ToUnixTimeSeconds(), authentication.Properties!.ExpiresUtc!.Value.ToUnixTimeSeconds());
        Assert.False(authentication.Properties.IsPersistent);
    }

    [Fact]
    public async Task LogoutIsPostOnlyAndClearsCookieWhenProviderFails()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var user = fixture.Create(Guid.NewGuid(), "access");
        var authentication = new CapturingAuthenticationService();
        using var services = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider();
        var page = new LogoutModel(fixture.Store, NullLogger<LogoutModel>.Instance)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(user), RequestServices = services },
            },
        };
        Assert.Equal(405, Assert.IsType<StatusCodeResult>(page.OnGet()).StatusCode);
        await page.OnPostAsync();
        Assert.True(authentication.SignedOut);
        Assert.False(fixture.Store.IsActive(user.SessionId, user.UserId));
    }

    [Fact]
    public async Task AlreadyCanceledLogoutStillInvalidatesLocalSessionAndClearsCookie()
    {
        var providerCalls = 0;
        using var fixture = new Fixture((_, _) =>
        {
            Interlocked.Increment(ref providerCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var user = fixture.Create(Guid.NewGuid(), "access");
        var authentication = new CapturingAuthenticationService();
        using var services = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var page = new LogoutModel(fixture.Store, NullLogger<LogoutModel>.Instance)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = Principal(user), RequestServices = services, RequestAborted = canceled.Token,
                },
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page.OnPostAsync());
        Assert.True(authentication.SignedOut);
        Assert.False(fixture.Store.IsActive(user.SessionId, user.UserId));
        Assert.Equal(0, providerCalls);
    }

    private static ClaimsPrincipal Principal(CreatedAppSession session) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
        new Claim(MiniTasksAuthentication.SessionIdClaim, session.SessionId),
    ], MiniTasksAuthentication.Scheme));

    private static HttpResponseMessage TokenResponse(Guid userId, string access, string refresh) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            access_token = access, refresh_token = refresh, expires_in = 3600,
            user = new { id = userId, email = "user@example.test" },
        }),
    };

    private sealed class Fixture : IDisposable
    {
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly Handler handler;
        public AdjustableClock Clock { get; } = new();
        public SupabaseAuthClient Auth { get; }
        public AppSessionStore Store { get; }

        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null)
        {
            handler = new Handler(respond ?? ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));
            Auth = new SupabaseAuthClient(new Factory(handler), Options.Create(new SupabaseOptions
            {
                Url = "https://synthetic.supabase.co", ApiKey = "synthetic-publishable",
            }), Clock);
            Store = new AppSessionStore(cache, Auth, Clock);
        }

        public CreatedAppSession Create(Guid userId, string access, bool expired = false) =>
            Store.Create(new SupabaseAuthSession(userId, "user@example.test", access, "refresh-old",
                Clock.GetUtcNow().AddMinutes(expired ? -1 : 120)));

        public void Dispose() { cache.Dispose(); handler.Dispose(); }
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://synthetic.supabase.co/"),
        };
    }

    private sealed class FixedAuthenticationState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class CapturingAuthenticationService : IAuthenticationService
    {
        public ClaimsPrincipal? SignedIn { get; private set; }
        public AuthenticationProperties? Properties { get; private set; }
        public bool SignedOut { get; private set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignedIn = principal; Properties = properties; return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOut = true; return Task.CompletedTask;
        }
    }
}
