using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MiniTasks.Web.Infrastructure;

namespace MiniTasks.Web.Authentication;

public sealed class SupabaseAuthClient(
    IHttpClientFactory clients, IOptions<SupabaseOptions> options, TimeProvider clock)
{
    public async Task<SupabaseAuthSession?> SignInAsync(
        string email, string password, CancellationToken cancellationToken = default)
    {
        return await RequestTokenAsync("password", new { email, password }, cancellationToken);
    }

    public async Task<SupabaseAuthSession> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken = default)
    {
        return await RequestTokenAsync(
            "refresh_token", new { refresh_token = refreshToken }, cancellationToken)
            ?? throw new SessionExpiredException();
    }

    public async Task<bool> SignOutAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var client = clients.CreateClient("supabase");
        using var request = CreateRequest(HttpMethod.Post, "auth/v1/logout?scope=local");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("apikey", options.Value.ApiKey);
        return request;
    }

    private async Task<SupabaseAuthSession?> RequestTokenAsync(
        string grant, object body, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient("supabase");
        using var request = CreateRequest(HttpMethod.Post, $"auth/v1/token?grant_type={grant}");
        request.Content = JsonContent.Create(body);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
                throw new AuthenticationUnavailableException();

            var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (result is null || result.User is null || result.User.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(result.AccessToken) ||
                string.IsNullOrWhiteSpace(result.RefreshToken) || result.ExpiresIn <= 0)
                throw new AuthenticationUnavailableException();

            return new SupabaseAuthSession(result.User.Id, result.User.Email ?? "",
                result.AccessToken, result.RefreshToken, clock.GetUtcNow().AddSeconds(result.ExpiresIn));
        }
        catch (HttpRequestException)
        {
            throw new AuthenticationUnavailableException();
        }
        catch (JsonException)
        {
            // The provider body can contain tokens. Do not include it or its parse exception in logs.
            throw new AuthenticationUnavailableException();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthenticationUnavailableException();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("user")] AuthUser? User);

    private sealed record AuthUser(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("email")] string? Email);
}
