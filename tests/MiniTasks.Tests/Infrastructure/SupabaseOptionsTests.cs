using MiniTasks.Web.Infrastructure;

namespace MiniTasks.Tests.Infrastructure;

public sealed class SupabaseOptionsTests
{
    [Theory]
    [InlineData("http://127.0.0.1:54421", true)]
    [InlineData("http://localhost:54421", true)]
    [InlineData("https://example.supabase.co", true)]
    [InlineData("http://example.supabase.co", false)]
    [InlineData("https://user:password@example.supabase.co", false)]
    [InlineData("https://example.supabase.co?token=test", false)]
    [InlineData("not-a-url", false)]
    public void OnlyHttpsOrLocalDevelopmentHttpAreAllowed(string url, bool expected) =>
        Assert.Equal(expected, SupabaseOptions.IsValid(new() { Url = url, ApiKey = "test-key" }));
}
