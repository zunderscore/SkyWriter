using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Communication;

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

    public SkyWriterSession(
        string audioDeviceId,
        string key,
        string obsWebsocketHost,
        int obsWebsocketPort,
        string obsWebsocketPassword,
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
        _azureKey = key;
        _azureRegion = azureRegion ?? _azureRegion;
        _language = language ?? _language;
        _profanityOption = profanityOption;

        _logger = logger;
    }

    public SpeechRecognizer? MainRecognizer { get; private set; }

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
        if (MainRecognizer is not null) await MainRecognizer.StopContinuousRecognitionAsync();
        if (_obsConnection is not null && _obsConnection.IsConnected) _obsConnection.Disconnect();
    }

    private void RunLoop()
    {
        _runLoop = true;

        while (_runLoop) { Thread.Sleep(250); }
    }

    private void InitializeRecognizer()
    {
        var speechConfig = SpeechConfig.FromSubscription(_azureKey, _azureRegion);
        speechConfig.SpeechRecognitionLanguage = _language;
        speechConfig.SetProfanity(_profanityOption);

        var mainAudioDeviceId = _audioDeviceId;
        var mainAudioConfig = AudioConfig.FromMicrophoneInput(mainAudioDeviceId);
        MainRecognizer = new SpeechRecognizer(speechConfig, mainAudioConfig);

        MainRecognizer.SessionStarted += (sender, args) =>
        {
            IsRecognizerRunning = true;
        };

        MainRecognizer.SessionStopped += (sender, args) =>
        {
            IsRecognizerRunning = false;
        };

        MainRecognizer.Recognized += (sender, args) =>
        {
            if (_isStreamRunning && _isMicActive && args.Result.Text.Trim() != String.Empty)
            {
                try
                {
                    _obsConnection.SendStreamCaption(args.Result.Text);
                    _logger?.LogInformation($"Recognized/sent to OBS: {args.Result.Text}");
                }
                catch (Exception ex)
                {
                    _logger?.LogInformation($"Error sending caption to OBS: {ex.Message}; {ex.InnerException?.Message}");
                }
            }
        };
    }

    private void InitializeObsConnection()
    {
        _obsConnection = new OBSWebsocketDotNet.OBSWebsocket();

        _obsConnection.Connected += OnObsConnected;
        _obsConnection.Disconnected += OnObsDisconnected;

        _obsConnection.StreamStateChanged += async (sender, args) =>
        {
            IsStreamRunning = args.OutputState.IsActive;
            await CheckMainRecognizerState();
        };

        _obsConnection.InputMuteStateChanged += async (sender, args) =>
        {
            if (args.InputName != "Microphone") return;

            IsMicActive = !args.InputMuted;
            await CheckMainRecognizerState();
        };
    }

    private async void OnObsConnected(object? sender, EventArgs e)
    {
        _isObsConnectionLoopRunning = false;
        _logger?.LogInformation("Connected to OBS");

        IsStreamRunning = _obsConnection.GetStreamStatus().IsActive;
        IsMicActive = !_obsConnection.GetInputMute("Microphone");

        await CheckMainRecognizerState();
    }

    private async void OnObsDisconnected(object? sender, ObsDisconnectionInfo e)
    {
        // We don't care if this fires because of a connection timeout while trying to connect
        if (!_isObsConnectionLoopRunning)
        {
            _logger?.LogInformation("Disconnected from OBS");

            await CheckMainRecognizerState();

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

    private async Task CheckMainRecognizerState()
    {
        if (MainRecognizer is null) return;

        if (_obsConnection.IsConnected && !IsRecognizerRunning && ShouldRunRecognizer)
        {
            await MainRecognizer.StartContinuousRecognitionAsync();

            _logger?.LogInformation("Recognition started");
        }
        else if (!_obsConnection.IsConnected || (IsRecognizerRunning && !ShouldRunRecognizer))
        {
            await MainRecognizer.StopContinuousRecognitionAsync();

            _logger?.LogInformation("Recognition stopped");
        }
    }
}