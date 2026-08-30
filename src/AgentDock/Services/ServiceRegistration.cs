using AgentDock.Services.Abstractions;
using AgentDock.Services.Remote;
using Microsoft.Extensions.DependencyInjection;

namespace AgentDock.Services;

/// <summary>
/// The application's composition root. Every service is registered against an interface so
/// its behaviour can be substituted — which is what makes a remote project host possible
/// without rewriting the panels that consume it.
///
/// Nothing in the app reaches for a service through a static member. The only statics that
/// remain are dependency-free pure functions (text transforms, icon tables, the slash-command
/// list) and WPF attached properties, neither of which has anything to substitute.
/// </summary>
public static class ServiceRegistration
{
    public static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        Register(services);
        return services.BuildServiceProvider();
    }

    public static void Register(IServiceCollection services)
    {
        // Platform / infrastructure
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IPerfDiagnostics, PerfDiagnostics>();
        services.AddSingleton<ISoundService, SoundService>();
        services.AddSingleton<ITaskbarIconService, TaskbarIconService>();

        // Settings and persistence
        services.AddSingleton<IAppSettingsStore, AppSettingsStore>();
        services.AddSingleton<IProjectSettingsStore, ProjectSettingsStore>();
        services.AddSingleton<IWorkspaceStore, WorkspaceStore>();

        // Theming
        services.AddSingleton<IThemeRegistry, ThemeRegistry>();
        services.AddSingleton<IThemeService, ThemeService>();

        // Agent
        services.AddSingleton<IClaudeEnvironment, ClaudeEnvironment>();
        services.AddSingleton<IClaudeSessionFactory, ClaudeSessionFactory>();
        services.AddSingleton<IAccountManager, AccountManager>();
        services.AddSingleton<IUsageService, UsageService>();

        // Presentation helpers
        services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        services.AddSingleton<IImageAttachmentService, ImageAttachmentService>();

        // Remote sessions. The server and client are singletons because a process hosts at
        // most one of each: one client at a time by design, and one listener per port.
        services.AddSingleton<RemoteIdentity>();
        services.AddSingleton<RemoteServerService>();
        services.AddSingleton<RemoteClientService>();

        // App lifecycle
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
        services.AddSingleton<IReleaseNotesService, ReleaseNotesService>();
    }
}
