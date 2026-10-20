using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using MiniTasks.Web.Authentication;
using MiniTasks.Web.Infrastructure;

namespace MiniTasks.Web.Tasks;

public sealed class TaskApiClient(
    IHttpClientFactory clients,
    ITokenProvider tokens,
    IOptions<SupabaseOptions> options,
    ILogger<TaskApiClient> logger)
{
    public const int ListLimit = 1000;
    private const string Selection = "select=id,title,is_completed,created_at";

    public Task<TaskItem[]> ListAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, $"rest/v1/tasks?{Selection}&order=id.desc&limit={ListLimit}", null, cancellationToken);

    public async Task<TaskItem> CreateAsync(string title, CancellationToken cancellationToken = default)
    {
        title = title.Trim();
        if (title.Length is < 1 or > 200)
            throw new ArgumentException("タイトルは1〜200文字で入力してください。", nameof(title));

        var rows = await SendAsync(HttpMethod.Post, $"rest/v1/tasks?{Selection}",
            new { title }, cancellationToken);
        return RequireSingle(rows);
    }

    public async Task<TaskItem> SetCompletedAsync(long id, bool completed,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        var rows = await SendAsync(HttpMethod.Patch,
            $"rest/v1/tasks?id=eq.{id.ToString(CultureInfo.InvariantCulture)}&{Selection}",
            new { is_completed = completed }, cancellationToken);
        return RequireSingle(rows);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        var rows = await SendAsync(HttpMethod.Delete,
            $"rest/v1/tasks?id=eq.{id.ToString(CultureInfo.InvariantCulture)}&{Selection}",
            null, cancellationToken);
        RequireSingle(rows);
    }

    private async Task<TaskItem[]> SendAsync(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken)
    {
        var accessToken = await tokens.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("apikey", options.Value.ApiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (method != HttpMethod.Get) request.Headers.Add("Prefer", "return=representation");
        if (body is not null) request.Content = JsonContent.Create(body);

        using var client = clients.CreateClient("supabase");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new SessionExpiredException();

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Task API returned HTTP {StatusCode}", (int)response.StatusCode);
            throw new TaskOperationException("タスクを保存・取得できませんでした。少し待ってから再試行してください。");
        }

        return await response.Content.ReadFromJsonAsync<TaskItem[]>(cancellationToken) ?? [];
    }

    private static TaskItem RequireSingle(TaskItem[] rows) => rows.Length == 1 ? rows[0] :
        throw new TaskOperationException("対象のタスクが見つかりません。最新の一覧を読み込んでください。");
}
