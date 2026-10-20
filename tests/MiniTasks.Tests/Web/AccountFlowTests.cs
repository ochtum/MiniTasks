using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MiniTasks.Tests.Web;

public sealed class AccountFlowTests
{
    [Fact]
    public async Task LoginRequiresAntiforgeryToken()
    {
        using var app = new SampleFactory();
        using var browser = app.CreateClient(new() { AllowAutoRedirect = false });
        using var response = await browser.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Email"] = "alice@example.test", ["Input.Password"] = "test-password" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Provider.LoginCount);
    }

    [Fact]
    public async Task LoginCookieReloadAndLogoutWorkWithoutExposingProviderTokens()
    {
        using var app = new SampleFactory();
        using var browser = app.CreateClient(new() { AllowAutoRedirect = false });
        var csrf = await AntiforgeryAsync(browser);
        using var login = await browser.PostAsync("/account/login", LoginForm(csrf));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.OriginalString);
        Assert.Contains("httponly", string.Join(";", login.Headers.GetValues("Set-Cookie")).ToLowerInvariant());
        Assert.DoesNotContain("test-access-token", string.Join(";", login.Headers.GetValues("Set-Cookie")));

        var html = await browser.GetStringAsync("/");
        Assert.Contains("alice@example.test", html);
        Assert.DoesNotContain("test-access-token", html);
        Assert.DoesNotContain("test-refresh-token", html);
        var reload = await browser.GetStringAsync("/");
        Assert.Contains("alice@example.test", reload);

        using var forbiddenLogout = await browser.PostAsync("/account/logout", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, forbiddenLogout.StatusCode);
        using var logout = await browser.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string,string>
        { ["__RequestVerificationToken"] = ExtractToken(reload) }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        using var afterLogout = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
        Assert.StartsWith("/account/login", afterLogout.Headers.Location?.PathAndQuery);
        Assert.Equal(1, app.Provider.LogoutCount);
    }

    [Fact]
    public async Task LoginReturnUrlCannotRedirectToAnotherSite()
    {
        using var app = new SampleFactory();
        using var browser = app.CreateClient(new() { AllowAutoRedirect = false });
        var csrf = await AntiforgeryAsync(browser);
        using var response = await browser.PostAsync("/account/login", LoginForm(csrf, "https://attacker.example"));
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task LogoutGetDoesNotChangeTheSession()
    {
        using var app = new SampleFactory();
        using var browser = app.CreateClient(new() { AllowAutoRedirect = false });
        using var response = await browser.GetAsync("/account/logout");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(0, app.Provider.LogoutCount);
    }

    [Fact]
    public async Task HealthRespondsWithoutAuthentication()
    {
        using var app = new SampleFactory();
        using var browser = app.CreateClient();
        using var response = await browser.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("MiniTasks", body.RootElement.GetProperty("app").GetString());
    }

    private static FormUrlEncodedContent LoginForm(string csrf, string returnUrl = "/") => new(new Dictionary<string,string>
    {
        ["Input.Email"] = "alice@example.test", ["Input.Password"] = "test-password",
        ["ReturnUrl"] = returnUrl, ["__RequestVerificationToken"] = csrf
    });

    private static async Task<string> AntiforgeryAsync(HttpClient client) =>
        ExtractToken(await client.GetStringAsync("/account/login"));

    private static string ExtractToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery hidden input was missing.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private sealed class SampleFactory : WebApplicationFactory<Program>
    {
        public FakeSupabase Provider { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string,string?>
            { ["Supabase:Url"] = "https://example.supabase.co", ["Supabase:ApiKey"] = "test-publishable" }));
            builder.ConfigureServices(services => services.AddHttpClient("supabase")
                .ConfigurePrimaryHttpMessageHandler(() => Provider));
        }
    }

    private sealed class FakeSupabase : HttpMessageHandler
    {
        public int LoginCount { get; private set; }
        public int LogoutCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            string body;
            if (path.StartsWith("/auth/v1/token"))
            {
                LoginCount++;
                body = "{\"access_token\":\"test-access-token\",\"refresh_token\":\"test-refresh-token\",\"expires_in\":3600,\"user\":{\"id\":\"11111111-1111-4111-8111-111111111111\",\"email\":\"alice@example.test\"}}";
            }
            else if (path == "/auth/v1/logout?scope=local") { LogoutCount++; body = "{}"; }
            else body = "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
