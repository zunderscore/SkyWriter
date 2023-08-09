# SkyWriter
## An Azure Cognitive Services-powered caption generator for OBS

SkyWriter uses Azure Cognitive Services speech-to-text to generate captions and send them to OBS. It will automatically start recognition when
it's connected to OBS and detects that a stream is active & your primary microphone source is unmuted. If you mute that source or stop streaming,
the session will automatically end.

### Requirements

- Windows 10, 1607+ or Windows 11
- OBS 28.0+
- An active Azure Cognitive Services instance

### Configuration

Running the app requires a few settings to be configured in `appsettings.json`:

- `AzureSpeechRegion`: The Azure region where your Cognitive Services instance. You can find this in your Cognitive Service under **Keys and Endpoint**. The default is `eastus`.
- `AzureSpeechKey`: The key used to connect to your Cognitive Services instance. You can find this in your Cognitive Service under **Keys and Endpoint**.
- `SpeechLanguage`: The language code for speech to be recognized. Supported language codes can be found [here](https://learn.microsoft.com/en-us/azure/cognitive-services/speech-service/language-support?tabs=stt-tts#speech-to-text). The default is `en-US`.
- `AzureSpeechProfanityLevel`: The level for how you want profanity to be handled. Levels and their corresponding numeric values can be found [here](https://learn.microsoft.com/en-us/dotnet/api/microsoft.cognitiveservices.speech.profanityoption). The default is `0` (Masked).
- `ObsWebsocketHost`: The hostname for your OBS Websocket connection. The default is `localhost`.
- `ObsWebsocketPort`: The port for your OBS Websocket connection. The default is `4455`.
- `ObsWebsocketPassword`: The password for your OBS Websocket connection.
- `ObsMicrophoneSourceName`: The name of your primary microphone source in OBS. The default is `Microphone`.
- `UseLocalSpeechEngine`: `true` if you want to use the Windows local speech recognition engine instead of Azure. NOTE: This ONLY works on Windows systems and is NOT as good as Azure.