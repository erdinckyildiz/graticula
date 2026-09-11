using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ASP.NET Core writes nothing per request at the default level, and an operator can still ask
/// it to.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-249](../../docs/architecture-debt.md)'s last measurement.</b> At the framework's own
/// default, every request wrote four lines from ASP.NET Core beside this server's redacted one,
/// and a server with no middleware at all outran `/healthz/live` 3.2× at 128 callers; at
/// `Warning`, 1.12×. These pin the three things the repair has to hold at once: the framework is
/// quiet by default, the raw-URL line stays off whatever is configured (ADR-015 §4.1), and a
/// configured level for the framework wins over the default — a diagnostic switch that the code
/// silently overrides is a switch that lies.
/// </para>
/// </remarks>
public sealed class TheFrameworkSpeaksAtWarningTests
{
    private const string Routing = "Microsoft.AspNetCore.Routing.EndpointMiddleware";
    private const string Result = "Microsoft.AspNetCore.Http.Result.OkObjectResult";
    private const string RawUrl = "Microsoft.AspNetCore.Hosting.Diagnostics";
    private const string Kestrel = "Microsoft.AspNetCore.Server.Kestrel";

    [Fact]
    public void By_default_the_framework_writes_nothing_per_request_and_this_server_does()
    {
        using ServiceProvider services = Build([]);
        ILoggerFactory loggers = services.GetRequiredService<ILoggerFactory>();

        Assert.False(loggers.CreateLogger(Routing).IsEnabled(LogLevel.Information));
        Assert.False(loggers.CreateLogger(Result).IsEnabled(LogLevel.Information));
        Assert.False(loggers.CreateLogger(RawUrl).IsEnabled(LogLevel.Information));

        // The redacted request line is this server's own category, and it stays.
        Assert.True(loggers.CreateLogger("requests").IsEnabled(LogLevel.Information));

        // A warning from the framework is still a warning.
        Assert.True(loggers.CreateLogger(Routing).IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void An_operator_who_asks_for_the_framework_gets_it_but_not_the_raw_url()
    {
        using ServiceProvider services = Build(new()
        {
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information",
            ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information",
        });

        ILoggerFactory loggers = services.GetRequiredService<ILoggerFactory>();

        Assert.True(loggers.CreateLogger(Routing).IsEnabled(LogLevel.Information));

        // Redaction is the code path, not a setting: the line that writes the raw query string
        // stays off even when a configuration names it by its own category.
        Assert.False(loggers.CreateLogger(RawUrl).IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void A_more_specific_setting_still_wins_as_ci_uses_it()
    {
        using ServiceProvider services = Build(new()
        {
            ["Logging:LogLevel:Microsoft.AspNetCore.Server.Kestrel"] = "Debug",
        });

        ILoggerFactory loggers = services.GetRequiredService<ILoggerFactory>();

        Assert.True(loggers.CreateLogger(Kestrel).IsEnabled(LogLevel.Debug));
        Assert.False(loggers.CreateLogger(Routing).IsEnabled(LogLevel.Information));
    }

    /// <summary>Logging as the host builds it: the configuration's rules, then this server's.</summary>
    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        ServiceCollection services = new();

        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));

            // <b>A provider that would write anything</b>, so that what is off is off because of
            // a filter. With no provider at all every level reads as disabled, and the first run of
            // these tests asserted on exactly that.
            logging.AddProvider(new Everything());

            Program.QuietTheFramework(logging, configuration);
        });

        return services.BuildServiceProvider();
    }

    private sealed class Everything : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        public void Dispose()
        {
        }
    }
}
