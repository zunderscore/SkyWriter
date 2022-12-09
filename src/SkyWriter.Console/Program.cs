using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using SkyWriter.Core;
using System.Reflection;

internal record AudioDevice(string DeviceName, string DeviceId);

internal class Program
{
    private static ILogger _logger = new Logger();

    private static IConfiguration? config = null;

    private static void Main(string[] args)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables();

        config = builder.Build();

        ShowSplash();

        var deviceId = InitializeAudioDevice();

        var obsPort = String.IsNullOrWhiteSpace(config?["ObsWebsocketPort"]) ? "4455" : config["ObsWebsocketPort"];
        var obsHost = String.IsNullOrWhiteSpace(config?["ObsWebsocketHost"]) ? "localhost" : config["ObsWebsocketHost"];

        var rawProfanityOption = 0;
        Int32.TryParse(config?["AzureSpeechProfanityLevel"], out rawProfanityOption);

        var session = new SkyWriterSession(
            deviceId,
            config?["AzureSpeechKey"] ?? String.Empty,
            obsHost ?? String.Empty,
            Int32.TryParse(obsPort, out var obsWebsocketPort) ? obsWebsocketPort : 4455,
            config?["ObsWebsocketPassword"] ?? String.Empty,
            config?["AzureSpeechRegion"],
            config?["AzureSpeechLanguage"],
            (ProfanityOption)rawProfanityOption,
            config?["ObsMicrophoneSourceName"] ?? "Microphone",
            _logger
        );

        Console.CancelKeyPress += async delegate (object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            await session.EndSession();
            _logger.LogInformation("Exiting SkyWriter");
            return;
        };

        session.StartSession();
    }

    private static void ShowSplash()
    {
        Console.Clear();
        Console.WriteLine("+----------------------------------------------------------------------------+");
        Console.WriteLine("|                                                                            |");
        Console.WriteLine("|   ███████ ██   ██ ██    ██ ██     ██ ██████  ██ ████████ ███████ ██████    |");
        Console.WriteLine("|   ██      ██  ██  ██    ██ ██     ██ ██   ██ ██    ██    ██      ██   ██   |");
        Console.WriteLine("|   ███████ █████     ████   ██  █  ██ ██████  ██    ██    █████   ██████    |");
        Console.WriteLine("|        ██ ██  ██     ██    ██ ███ ██ ██   ██ ██    ██    ██      ██   ██   |");
        Console.WriteLine("|   ███████ ██   ██    ██     ███ ███  ██   ██ ██    ██    ███████ ██   ██   |");
        Console.WriteLine("|                                                                            |");
        Console.WriteLine("+----------------------------------------------------------------------------+");
        Console.WriteLine();
        _logger.LogInformation($"SkyWriter Version {Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion} started");
        Console.WriteLine();
    }

    private static string InitializeAudioDevice()
    {
        var deviceList = new Dictionary<int, AudioDevice>();

        int x = 0;
        foreach (var device in (new MMDeviceEnumerator()).EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).OrderBy(d => d.FriendlyName).ToList())
        {
            deviceList.Add(++x, new AudioDevice(device.FriendlyName, device.ID));
        }

        Console.WriteLine("Audio Devices");
        foreach (var device in deviceList)
        {
            Console.WriteLine($"[{device.Key}] {device.Value.DeviceName}");
        }

        string? mainDeviceChoice = null;
        int mainDeviceChoiceNumber;

        while (mainDeviceChoice is null || !int.TryParse(mainDeviceChoice, out mainDeviceChoiceNumber))
        {
            Console.Write("Choose main input device: ");
            mainDeviceChoice = Console.ReadLine();
        }

        Console.WriteLine();
        _logger.LogInformation($"Will read audio input from device \"{deviceList[mainDeviceChoiceNumber].DeviceName}\"");

        return deviceList[mainDeviceChoiceNumber].DeviceId;
    }

}

internal class Logger : ILogger
{
    public Logger()
    {
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "logs"));
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var logLevelString = String.Empty;

        switch (logLevel)
        {
            case LogLevel.Critical:
                logLevelString = "critical";
                break;

            case LogLevel.Error:
                logLevelString = "error";
                break;

            case LogLevel.Warning:
                logLevelString = "warning";
                break;

            case LogLevel.Trace:
                logLevelString = "trace";
                break;

            case LogLevel.Debug:
                logLevelString = "debug";
                break;

            case LogLevel.Information:
            default:
                logLevelString = "info";
                break;
        }

        var formattedLogMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {logLevelString}: {formatter(state, exception)}";
        var logFileName = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "logs", $"{DateTime.Now:yyyy-MM-dd}.log");

        Console.WriteLine(formattedLogMessage);
        File.AppendAllText(logFileName, formattedLogMessage + Environment.NewLine);
    }
}
