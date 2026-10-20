using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiniTasks.Web.Authentication;
using MiniTasks.Web.Infrastructure;
using MiniTasks.Web.Tasks;

namespace MiniTasks.Tests.Tasks;

public sealed class TaskApiClientTests
{
    private const string Row = "[{\"id\":1,\"title\":\"demo\",\"is_completed\":false,\"created_at\":\"2026-10-19T00:00:00Z\"}]";

    [Fact]
    public async Task CreateTrimsTitleAndLeavesOwnerToAuthenticatedDatabase()
    {
        var handler = new RecordingHandler(Row);
        var sut = Create(handler, new FixedToken("user-a"));
        var task = await sut.CreateAsync("  demo  ");

        Assert.Equal(1, task.Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer user-a", request.Authorization);
        Assert.Equal("test-publishable", request.ApiKey);
        Assert.Equal("return=representation", request.Prefer);
        using var json = JsonDocument.Parse(request.Body!);
        Assert.Equal("demo", json.RootElement.GetProperty("title").GetString());
        Assert.False(json.RootElement.TryGetProperty("owner_id", out _));
    }

    [Fact]
    public async Task SeparateUsersDoNotShareAuthorizationHeaders()
    {
        var handler = new RecordingHandler(Row);
        await Create(handler, new FixedToken("user-a")).ListAsync();
        await Create(handler, new FixedToken("user-b")).ListAsync();
        Assert.Equal(new[] { "Bearer user-a", "Bearer user-b" }, handler.Requests.Select(r => r.Authorization));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidTitleDoesNotSendHttpRequest(string title)
    {
        var handler = new RecordingHandler(Row);
        await Assert.ThrowsAsync<ArgumentException>(() => Create(handler).CreateAsync(title));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TooLongTitleDoesNotSendHttpRequest()
    {
        var handler = new RecordingHandler(Row);
        await Assert.ThrowsAsync<ArgumentException>(() => Create(handler).CreateAsync(new string('a', 201)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnauthorizedResponseRequiresLogin()
    {
        var handler = new RecordingHandler("{}", HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<SessionExpiredException>(() => Create(handler).ListAsync());
    }

    [Fact]
    public async Task HiddenOrDeletedRowIsNotReportedAsSuccessfulUpdate()
    {
        var handler = new RecordingHandler("[]");
        await Assert.ThrowsAsync<TaskOperationException>(() => Create(handler).SetCompletedAsync(99, true));
        Assert.Contains("id=eq.99", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task DeleteChecksThatTheDatabaseActuallyDeletedOneRow()
    {
        var handler = new RecordingHandler("[]");
        await Assert.ThrowsAsync<TaskOperationException>(() => Create(handler).DeleteAsync(99));
    }

    private static TaskApiClient Create(RecordingHandler handler, ITokenProvider? tokens = null) => new(
        new ClientFactory(handler), tokens ?? new FixedToken("user-a"),
        Options.Create(new SupabaseOptions { Url = "https://example.supabase.co", ApiKey = "test-publishable" }),
        NullLogger<TaskApiClient>.Instance);

    private sealed class FixedToken(string token) : ITokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        { BaseAddress = new Uri("https://example.supabase.co/") };
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Authorization,
        string ApiKey, string? Prefer, string? Body);

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(), request.Headers.GetValues("apikey").Single(),
                request.Headers.TryGetValues("Prefer", out var values) ? values.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
