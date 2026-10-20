namespace MiniTasks.Web.Infrastructure;

public sealed class SupabaseOptions
{
    public const string SectionName = "Supabase";
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";

    public static bool IsValid(SupabaseOptions options) =>
        !string.IsNullOrWhiteSpace(options.ApiKey) &&
        Uri.TryCreate(options.Url, UriKind.Absolute, out var uri) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        (uri.Scheme == Uri.UriSchemeHttps ||
         (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}
