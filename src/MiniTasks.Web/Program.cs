using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using MiniTasks.Web.Authentication;
using MiniTasks.Web.Components;
using MiniTasks.Web.Infrastructure;
using MiniTasks.Web.Tasks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<SupabaseOptions>()
    .BindConfiguration(SupabaseOptions.SectionName)
    .Validate(SupabaseOptions.IsValid,
        "Supabase:Url と Supabase:ApiKey を設定してください。URLはHTTPSまたはローカルHTTPを使用します。")
    .ValidateOnStart();
builder.Services.AddHttpClient("supabase", (services, client) =>
{
    var options = services.GetRequiredService<IOptions<SupabaseOptions>>().Value;
    client.BaseAddress = new Uri(options.Url.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMiniTasksAuthentication();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<TaskApiClient>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    foreach (var name in new[] { "auth-login", "auth-logout" })
        options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = name == "auth-login" ? 20 : 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorPages();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", app = "MiniTasks" }))
    .AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
