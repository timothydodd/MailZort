using MailZort.Components;
using MailZort.Data;
using MailZort.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.Sources.Clear();
builder.Configuration.SetBasePath(AppContext.BaseDirectory);
builder.Configuration.AddJsonFile("appsettings.json", optional: false);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

var emailConfig = new EmailSettings();
builder.Configuration.GetSection("EmailSettings").Bind(emailConfig);

if (string.IsNullOrWhiteSpace(emailConfig.Server) ||
    string.IsNullOrWhiteSpace(emailConfig.Username) ||
    string.IsNullOrWhiteSpace(emailConfig.Password))
{
    throw new InvalidOperationException("EmailSettings: Server, Username, and Password are required.");
}

builder.Services.AddSingleton(emailConfig);

// Data layer. Rules, senders, activity and portal logins all live in the database now;
// appsettings.json only supplies connection details and first-run defaults.
builder.Services.AddSingleton<IMailZortDatabase, MailZortDatabase>();
builder.Services.AddSingleton<ISettingsStore, SettingsStore>();
builder.Services.AddSingleton<IActivityStore, ActivityStore>();
builder.Services.AddSingleton<ISenderStore, SenderStore>();
builder.Services.AddSingleton<IRuleStore, RuleStore>();
builder.Services.AddSingleton<IMessageLocationStore, MessageLocationStore>();
builder.Services.AddSingleton<IPendingActionStore, PendingActionStore>();
builder.Services.AddSingleton<IUserStore, UserStore>();
builder.Services.AddSingleton<LegacyMigrator>();

// Mail processing
builder.Services.AddSingleton<IBatchRuleProcessor, BatchRuleProcessor>();
builder.Services.AddSingleton<IEmailMover, EmailMover>();
builder.Services.AddSingleton<RuleMatcher>();
builder.Services.AddSingleton<MailServiceStatus>();
builder.Services.AddSingleton<PortalActions>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddHostedService<EmailMonitoringService>();

// Portal
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.Name = "mailzort.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Bring the database up before anything reads from it.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IMailZortDatabase>();
    await db.InitializeAsync();

    await scope.ServiceProvider.GetRequiredService<LegacyMigrator>().MigrateAsync();

    var settings = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
    await settings.SeedDefaultsAsync(emailConfig, CancellationToken.None);
    await settings.LoadAsync(CancellationToken.None);

    await scope.ServiceProvider.GetRequiredService<IUserStore>().SeedDefaultUserAsync();
    await scope.ServiceProvider.GetRequiredService<IRuleStore>().LoadAsync(CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<ISenderStore>().LoadAsync(CancellationToken.None);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPortalEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

Console.WriteLine("MailZort starting - portal and mail monitoring in one process.");

await app.RunAsync();

public partial class Program;
