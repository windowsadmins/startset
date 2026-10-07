using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;
using StartSet.Infrastructure.Security;
using StartSet.Service.Workers;

namespace StartSet.Service;

/// <summary>
/// StartSet Windows Service entry point.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        // Lock the data folder down before anything in it is read: the service runs as
        // SYSTEM and acts on Config.yaml and the payload folders, so none of them may be
        // writable by a standard user. Notes are logged once the logger is up.
        var guardNotes = DataDirectoryGuard.Secure();

        // Initialize preferences
        var preferencesService = new PreferencesService();
        preferencesService.Load();

        // Initialize logging for service mode
        StartSetLogger.Initialize(preferencesService.Preferences, isService: true);

        try
        {
            StartSetLogger.Information("StartSet Service starting");
            foreach (var note in guardNotes)
                StartSetLogger.Warning("Data directory: {Note}", note);

            var builder = Host.CreateApplicationBuilder(args);

            // Register services
            builder.Services.AddSingleton(preferencesService);
            
            // Add workers
            builder.Services.AddHostedService<TriggerWatcherWorker>();
            builder.Services.AddHostedService<LogonEventWorker>();
            builder.Services.AddHostedService<BootWorker>();

            // Configure as Windows Service
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "StartSet";
            });

            var host = builder.Build();
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            StartSetLogger.Fatal(ex, "StartSet Service failed to start");
            throw;
        }
        finally
        {
            StartSetLogger.Information("StartSet Service stopped");
            StartSetLogger.CloseAndFlush();
        }
    }
}
