using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Communication;
using System.Speech.Recognition;

namespace SkyWriter.Core;

public class SkyWriterSession
{
    private ILogger? _logger;
    private bool _runLoop = false;
    private bool _isObsConnectionLoopRunning = false;

    private string _audioDeviceId = String.Empty;
    private string _azureKey = String.Empty;
    private string _azureRegion = "eastus";
    private string _language = "en-US";
    private ProfanityOption _profanityOption = ProfanityOption.Masked;

    private string _obsWebsocketHost = String.Empty;
    private int _obsWebsocketPort = 0;
    private string _obsWebsocketPassword = String.Empty;
    private string _obsMicInputName = "Microphone";

    private bool _useLocalSpeechEngine = false;

    public SkyWriterSession(
        string audioDeviceId,
        string key,
        string obsWebsocketHost,
        int obsWebsocketPort,
        string obsWebsocketPassword,
        bool useLocalSpeechEngine,
        string? azureRegion = null,
        string? language = null,
        ProfanityOption profanityOption = ProfanityOption.Masked,
        string? obsMicInputName = null,
        ILogger? logger = null
    )
    {
        _obsWebsocketHost = obsWebsocketHost;
        _obsWebsocketPort = obsWebsocketPort;
        _obsWebsocketPassword = obsWebsocketPassword;
        _obsMicInputName = obsMicInputName ?? _obsMicInputName;

        _audioDeviceId = audioDeviceId;

        _useLocalSpeechEngine = useLocalSpeechEngine;

        if (_useLocalSpeechEngine && !System.OperatingSystem.IsWindows())
        {
            throw new ArgumentException("useLocalSpeechEngine can only be 'true' on Windows platforms. All other platforms must set 'false' to use Azure Speech.");
        }

        _azureKey = key;
        _azureRegion = azureRegion ?? _azureRegion;
        _language = language ?? _language;
        _profanityOption = profanityOption;

        _logger = logger;
    }

    public Microsoft.CognitiveServices.Speech.SpeechRecognizer? AzureRecognizer { get; private set; }
    public System.Speech.Recognition.SpeechRecognitionEngine? LocalRecognizer { get; private set; }

    private OBSWebsocket _obsConnection = new();
    public OBSWebsocket ObsConnection => _obsConnection;

    private bool _isStreamRunning = false;
    private bool IsStreamRunning
    {
        get => _isStreamRunning;
        set
        {
            if (value != _isStreamRunning)
            {
                _isStreamRunning = value;
                _logger?.LogInformation($"OBS: Stream {(_isStreamRunning ? "" : "no longer ")}active");
            }
        }
    }

    private bool _isMicActive = false;
    private bool IsMicActive
    {
        get => _isMicActive;
        set
        {
            if (value != _isMicActive)
            {
                _isMicActive = value;
                _logger?.LogInformation($"OBS: Audio source {(_isMicActive ? "un" : "")}muted");
            }
        }
    }

    private bool ShouldRunRecognizer => _isStreamRunning && _isMicActive;

    public bool IsRecognizerRunning { get; private set; }

    public void StartSession()
    {
        InitializeRecognizer();
        InitializeObsConnection();
        AttemptObsConnection();

        var workerThread = new Thread(RunLoop);
        workerThread.Start();
    }

    public async Task EndSession()
    {
        _runLoop = false;
        _logger?.LogInformation("Cleaning up...");
        if (AzureRecognizer is not null) await AzureRecognizer.StopContinuousRecognitionAsync();
        if (_obsConnection is not null && _obsConnection.IsConnected) _obsConnection.Disconnect();
    }

    private void RunLoop()
    {
        _runLoop = true;

        while (_runLoop) { Thread.Sleep(250); }
    }

    private void InitializeRecognizer()
    {
        if (_useLocalSpeechEngine)
        {
            _logger?.LogInformation("Using local speech engine");

            // We already check that it's Windows on startup, so no need to warn here.
#pragma warning disable CA1416
            LocalRecognizer = new System.Speech.Recognition.SpeechRecognitionEngine(
                new System.Globalization.CultureInfo(_language)
            );

            LocalRecognizer.LoadGrammar(new DictationGrammar());

            LocalRecognizer.SetInputToDefaultAudioDevice();

            LocalRecognizer.SpeechRecognized += (sender, args) =>
            {
                SendTextToObs(args.Result.Text);
            };
#pragma warning restore CA1416
        }
        else
        {
            _logger?.LogInformation("Using Azure speech engine");

            var speechConfig = SpeechConfig.FromSubscription(_azureKey, _azureRegion);
            speechConfig.SpeechRecognitionLanguage = _language;
            speechConfig.SetProfanity(_profanityOption);

            var mainAudioDeviceId = _audioDeviceId;
            var mainAudioConfig = AudioConfig.FromMicrophoneInput(mainAudioDeviceId);
            AzureRecognizer = new Microsoft.CognitiveServices.Speech.SpeechRecognizer(speechConfig, mainAudioConfig);

            AzureRecognizer.SessionStarted += (sender, args) =>
            {
                IsRecognizerRunning = true;
            };

            AzureRecognizer.SessionStopped += (sender, args) =>
            {
                IsRecognizerRunning = false;
            };

            AzureRecognizer.Recognized += (sender, args) =>
            {
                SendTextToObs(args.Result.Text);
            };
        }
    }

    private void InitializeObsConnection()
    {
        _obsConnection = new OBSWebsocketDotNet.OBSWebsocket();

        _obsConnection.Connected += OnObsConnected;
        _obsConnection.Disconnected += OnObsDisconnected;

        _obsConnection.StreamStateChanged += async (sender, args) =>
        {
            IsStreamRunning = args.OutputState.IsActive;
            await CheckRecognizerState();
        };

        _obsConnection.InputMuteStateChanged += async (sender, args) =>
        {
            if (args.InputName != "Microphone") return;

            IsMicActive = !args.InputMuted;
            await CheckRecognizerState();
        };
    }

    private async void OnObsConnected(object? sender, EventArgs e)
    {
        _isObsConnectionLoopRunning = false;
        _logger?.LogInformation("Connected to OBS");

        IsStreamRunning = _obsConnection.GetStreamStatus().IsActive;
        IsMicActive = !_obsConnection.GetInputMute("Microphone");

        await CheckRecognizerState();
    }

    private async void OnObsDisconnected(object? sender, ObsDisconnectionInfo e)
    {
        // We don't care if this fires because of a connection timeout while trying to connect
        if (!_isObsConnectionLoopRunning)
        {
            _logger?.LogInformation("Disconnected from OBS");

            await CheckRecognizerState();

            _logger?.LogInformation("Waiting for OBS connection to close...");

            while (_obsConnection.IsConnected)
            {
                await Task.Delay(200);
            }

            _logger?.LogInformation("OBS connection closed");
        }

        AttemptObsConnection();
    }

    private void AttemptObsConnection()
    {
        if (!_isObsConnectionLoopRunning)
        {
            _logger?.LogInformation("Waiting for OBS connection...");
            _isObsConnectionLoopRunning = true;
        }

        if (!_obsConnection.IsConnected)
        {
            _obsConnection.ConnectAsync($"ws://{_obsWebsocketHost}:{_obsWebsocketPort}", _obsWebsocketPassword);
        }
    }

    private void SendTextToObs(string text)
    {
        if (_isStreamRunning && _isMicActive && text.Trim() != String.Empty)
        {
            try
            {
                _obsConnection.SendStreamCaption(text);
                _logger?.LogInformation($"Recognized/sent to OBS: {text}");
            }
            catch (Exception ex)
            {
                _logger?.LogInformation($"Error sending caption to OBS: {ex.Message}; {ex.InnerException?.Message}");
            }
        }
    }

    private async Task CheckRecognizerState()
    {
        if (AzureRecognizer is null && LocalRecognizer is null) return;

        if (_obsConnection.IsConnected && !IsRecognizerRunning && ShouldRunRecognizer)
        {
            if (_useLocalSpeechEngine)
            {
                if (LocalRecognizer is null) return;
                IsRecognizerRunning = true;
#pragma warning disable CA1416
                LocalRecognizer.RecognizeAsync(RecognizeMode.Multiple);
#pragma warning restore CA1416
            }
            else
            {
                if (AzureRecognizer is null) return;
                await AzureRecognizer.StartContinuousRecognitionAsync();
            }

            _logger?.LogInformation("Recognition started");
        }
        else if (!_obsConnection.IsConnected || (IsRecognizerRunning && !ShouldRunRecognizer))
        {
            if (_useLocalSpeechEngine)
            {
                if (LocalRecognizer is null) return;
#pragma warning disable CA1416
                LocalRecognizer.RecognizeAsyncStop();
#pragma warning restore CA1416
                IsRecognizerRunning = false;
            }
            else
            {
                if (AzureRecognizer is null) return;
                await AzureRecognizer.StopContinuousRecognitionAsync();
            }

            _logger?.LogInformation("Recognition stopped");
        }
    }
}