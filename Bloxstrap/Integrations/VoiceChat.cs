using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;

using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Bloxstrap.Integrations
{
    /// <summary>
    /// Voice chat through the FluxStrap voice server (VoiceServer/ in the repository), so Fluxer itself doesn't need
    /// to change. Everyone in the same game server shares a room. Every build can listen; dev builds can talk if the
    /// server accepts their dev key, which the server checks itself, so a modified public build still can't talk.
    /// </summary>
    public class VoiceChat : IDisposable
    {
        public const string DefaultServer = "wss://fluxstrap.onrender.com/voice";

        public static readonly IReadOnlyDictionary<string, int> PushToTalkKeys = new Dictionary<string, int>
        {
            { "V", 0x56 },
            { "B", 0x42 },
            { "G", 0x47 },
            { "Caps Lock", 0x14 },
            { "Left Alt", 0xA4 },
            { "Mouse button 4", 0x05 },
            { "Mouse button 5", 0x06 }
        };

        private const int SampleRate = 48000;
        private const int FrameSamples = SampleRate / 50; // 20 ms, what the server expects per message
        private const int MaxPacketBytes = 1275;

        private readonly ActivityWatcher _activityWatcher;

        private readonly object _sessionLock = new();
        private CancellationTokenSource? _session;
        private Channel<(WebSocketMessageType Type, byte[] Data)>? _outgoing;

        private readonly MixingSampleProvider _mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)) { ReadFully = true };
        private readonly VolumeSampleProvider _volume;
        private WaveOutEvent? _output;

        private readonly Dictionary<uint, Speaker> _speakers = new();
        private readonly HashSet<string> _announcedRooms = new();

        private bool _disposed = false;

        public string Status { get; private set; } = "Not in a game";

        /// <summary>
        /// Short messages worth showing as a notification
        /// </summary>
        public event EventHandler<string>? OnNotice;

        private class Speaker
        {
            public string Name = "Someone";
            public bool Talking;
            public readonly IOpusDecoder Decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);
            public readonly BufferedWaveProvider Buffer = new(new WaveFormat(SampleRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true
            };
            public ISampleProvider? Input;
        }

        public VoiceChat(ActivityWatcher activityWatcher)
        {
            _activityWatcher = activityWatcher;
            _volume = new(_mixer) { Volume = Math.Clamp(App.Settings.Prop.VoiceChatVolume, 0, 200) / 100f };

            _activityWatcher.OnGameJoin += (_, _) => Join();
            _activityWatcher.OnGameLeave += (_, _) => Leave();
        }

        private static string ServerUrl => String.IsNullOrWhiteSpace(App.Settings.Prop.VoiceChatServer) ? DefaultServer : App.Settings.Prop.VoiceChatServer;

        private void Join()
        {
            const string LOG_IDENT = "VoiceChat::Join";

            var data = _activityWatcher.Data;

            // studio test servers and some legacy joins report an empty job id
            if (data.PlaceId == 0 || String.IsNullOrEmpty(data.JobId) || data.JobId.Trim('0', '-').Length == 0)
            {
                App.Logger.WriteLine(LOG_IDENT, "No job id for this server, not joining voice chat");
                return;
            }

            string room = $"{data.PlaceId}/{data.JobId}";

            Leave();

            var session = new CancellationTokenSource();

            lock (_sessionLock)
                _session = session;

            App.Logger.WriteLine(LOG_IDENT, $"Joining voice chat for {room}");

            Task.Run(() => RunSession(room, data.UserId, session.Token));
        }

        private void Leave()
        {
            CancellationTokenSource? session;

            lock (_sessionLock)
            {
                session = _session;
                _session = null;
            }

            if (session is null)
                return;

            session.Cancel();
            session.Dispose();

            Status = "Not in a game";
        }

        private async Task RunSession(string room, long userId, CancellationToken token)
        {
            const string LOG_IDENT = "VoiceChat::RunSession";

            int failures = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RunConnection(room, userId, token);
                    failures = 0;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Voice connection dropped ({ex.GetType().Name}: {ex.Message})");
                    failures++;
                }

                Status = "Reconnecting";

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(failures, 4))), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            App.Logger.WriteLine(LOG_IDENT, $"Left voice chat for {room}");
        }

        private async Task RunConnection(string room, long userId, CancellationToken token)
        {
            const string LOG_IDENT = "VoiceChat::RunConnection";

            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            Status = "Connecting";

            // free hosting puts the server to sleep when nobody's using it, waking up can take most of a minute
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(75));
                await socket.ConnectAsync(new Uri(ServerUrl), connectTimeout.Token);
            }

            var outgoing = Channel.CreateBounded<(WebSocketMessageType, byte[])>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
            _outgoing = outgoing;

            var sendTask = Task.Run(async () =>
            {
                await foreach (var (type, data) in outgoing.Reader.ReadAllAsync(token))
                    await socket.SendAsync(data, type, true, token);
            }, token);

            string key = App.IsDevBuild ? App.Settings.Prop.VoiceChatKey.Trim() : "";
            Queue(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new { t = "hello", v = 1, room, user = userId, key }));

            StartPlayback();

            try
            {
                var buffer = new byte[16 * 1024];
                using var message = new MemoryStream();

                while (socket.State == WebSocketState.Open)
                {
                    message.SetLength(0);

                    WebSocketReceiveResult result;

                    do
                    {
                        result = await socket.ReceiveAsync(buffer, token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            App.Logger.WriteLine(LOG_IDENT, $"Server closed the connection ({result.CloseStatus} {result.CloseStatusDescription})");
                            return;
                        }

                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Binary)
                        OnAudio(message.GetBuffer(), (int)message.Length);
                    else
                        OnMessage(room, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }
            }
            finally
            {
                _outgoing = null;
                outgoing.Writer.TryComplete();

                StopCapture();
                ClearSpeakers();

                try
                {
                    await sendTask;
                }
                catch (Exception)
                {
                    // the socket's gone either way
                }
            }
        }

        private void Queue(WebSocketMessageType type, byte[] data) => _outgoing?.Writer.TryWrite((type, data));

        private void OnMessage(string room, string text)
        {
            const string LOG_IDENT = "VoiceChat::OnMessage";

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            switch (root.GetProperty("t").GetString())
            {
                case "welcome":
                    bool canTalk = root.GetProperty("speaker").GetBoolean();
                    App.Logger.WriteLine(LOG_IDENT, $"Connected to voice chat ({(canTalk ? "can talk" : "listening")})");

                    if (canTalk)
                        StartCapture();
                    else if (App.IsDevBuild && !String.IsNullOrWhiteSpace(App.Settings.Prop.VoiceChatKey))
                        OnNotice?.Invoke(this, "Your voice key wasn't accepted, so you can only listen. Check it in FluxStrap's Integrations settings.");
                    break;

                case "room":
                    var listed = new HashSet<uint>();

                    lock (_speakers)
                    {
                        foreach (var entry in root.GetProperty("speakers").EnumerateArray())
                        {
                            uint id = entry.GetProperty("id").GetUInt32();
                            listed.Add(id);
                            GetSpeaker(id).Name = entry.GetProperty("name").GetString() ?? "Someone";
                        }

                        foreach (uint id in _speakers.Keys.Where(x => !listed.Contains(x)).ToList())
                            RemoveSpeaker(id);
                    }

                    int listeners = root.GetProperty("listeners").GetInt32();
                    UpdateStatus(listeners);

                    string[] names = GetSpeakerNames();

                    if (names.Length > 0 && _announcedRooms.Add(room))
                        OnNotice?.Invoke(this, $"{String.Join(", ", names)} {(names.Length == 1 ? "is" : "are")} on voice chat in this server.");
                    break;

                case "talk":
                    lock (_speakers)
                        GetSpeaker(root.GetProperty("id").GetUInt32()).Talking = root.GetProperty("on").GetBoolean();
                    break;

                case "error":
                    App.Logger.WriteLine(LOG_IDENT, $"Server error: {root.GetProperty("message").GetString()}");
                    break;
            }
        }

        private void UpdateStatus(int listeners)
        {
            string[] names = GetSpeakerNames();

            Status = names.Length == 0
                ? $"Connected, {listeners} in voice"
                : $"Connected, {listeners} in voice, {String.Join(", ", names)} can talk";
        }

        private string[] GetSpeakerNames()
        {
            lock (_speakers)
                return _speakers.Values.Select(x => x.Name).Distinct().ToArray();
        }

        #region Playback
        private void StartPlayback()
        {
            if (_output is not null || !App.Settings.Prop.VoiceChatEnabled)
                return;

            _output = new WaveOutEvent { DesiredLatency = 150 };
            _output.Init(_volume);
            _output.Play();
        }

        private void StopPlayback()
        {
            _output?.Stop();
            _output?.Dispose();
            _output = null;
        }

        private void OnAudio(byte[] data, int length)
        {
            if (length <= 4)
                return;

            uint id = BitConverter.ToUInt32(data, 0);
            var pcm = new short[FrameSamples];

            lock (_speakers)
            {
                var speaker = GetSpeaker(id);

                int samples;

                try
                {
                    samples = speaker.Decoder.Decode(data.AsSpan(4, length - 4), pcm, FrameSamples, false);
                }
                catch (Exception)
                {
                    return; // a corrupt frame, drop it
                }

                if (samples <= 0)
                    return;

                var bytes = new byte[samples * 2];
                Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                speaker.Buffer.AddSamples(bytes, 0, bytes.Length);
            }
        }

        // callers hold _speakers
        private Speaker GetSpeaker(uint id)
        {
            if (!_speakers.TryGetValue(id, out var speaker))
            {
                speaker = new Speaker();
                speaker.Input = speaker.Buffer.ToSampleProvider();
                _mixer.AddMixerInput(speaker.Input);
                _speakers[id] = speaker;
            }

            return speaker;
        }

        private void RemoveSpeaker(uint id)
        {
            if (_speakers.Remove(id, out var speaker) && speaker.Input is not null)
                _mixer.RemoveMixerInput(speaker.Input);
        }

        private void ClearSpeakers()
        {
            lock (_speakers)
            {
                foreach (uint id in _speakers.Keys.ToList())
                    RemoveSpeaker(id);
            }
        }
        #endregion

        #region Capture
#if DEV_BUILD
        private WaveInEvent? _input;
        private IOpusEncoder? _encoder;
        private readonly short[] _frame = new short[FrameSamples];
        private int _frameFill = 0;
        private bool _transmitting = false;
        private int _gateHold = 0;

        private void StartCapture()
        {
            const string LOG_IDENT = "VoiceChat::StartCapture";

            if (_input is not null)
                return;

            try
            {
                _encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
                _encoder.Bitrate = 32000;

                _input = new WaveInEvent
                {
                    DeviceNumber = App.Settings.Prop.VoiceChatMicrophone,
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = 20
                };

                _input.DataAvailable += OnMicrophoneData;
                _input.StartRecording();

                App.Logger.WriteLine(LOG_IDENT, App.Settings.Prop.VoiceChatOpenMic ? "Microphone on (open mic)" : "Microphone on (push to talk)");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                OnNotice?.Invoke(this, "Couldn't open your microphone. Check Windows' microphone privacy settings, or pick another one in FluxStrap.");
                StopCapture();
            }
        }

        private void StopCapture()
        {
            if (_input is null)
                return;

            _input.DataAvailable -= OnMicrophoneData;

            try
            {
                _input.StopRecording();
            }
            catch (Exception)
            {
                // already stopped
            }

            _input.Dispose();
            _input = null;
            _transmitting = false;
            _frameFill = 0;
        }

        private void OnMicrophoneData(object? sender, WaveInEventArgs e)
        {
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                _frame[_frameFill++] = BitConverter.ToInt16(e.Buffer, i);

                if (_frameFill == FrameSamples)
                {
                    _frameFill = 0;
                    OnFrame();
                }
            }
        }

        private void OnFrame()
        {
            bool transmit = ShouldTransmit();

            if (transmit != _transmitting)
            {
                _transmitting = transmit;
                Queue(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new { t = "talk", on = transmit }));
            }

            if (!transmit || _encoder is null)
                return;

            var packet = new byte[MaxPacketBytes];
            int length = _encoder.Encode(_frame, FrameSamples, packet, packet.Length);

            if (length > 0)
                Queue(WebSocketMessageType.Binary, packet[..length]);
        }

        private bool ShouldTransmit()
        {
            if (!App.Settings.Prop.VoiceChatOpenMic)
                return (GetAsyncKeyState(App.Settings.Prop.VoiceChatPushToTalkKey) & 0x8000) != 0 && IsClientFocused();

            // open mic: a simple noise gate that stays open for 300 ms after you stop talking
            double sum = 0;

            foreach (short sample in _frame)
                sum += (double)sample * sample;

            double rms = Math.Sqrt(sum / FrameSamples) / short.MaxValue;

            if (rms > 0.015)
                _gateHold = 15;
            else if (_gateHold > 0)
                _gateHold--;

            return _gateHold > 0;
        }

        private static bool IsClientFocused()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);

            try
            {
                using var process = Process.GetProcessById((int)pid);
                string name = process.ProcessName + ".exe";

                return name.Equals(App.FluxerPlayerAppName, StringComparison.OrdinalIgnoreCase) || name.Equals(App.RobloxPlayerAppName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
#else
        // public builds have no microphone code at all
        private void StartCapture() { }

        private void StopCapture() { }
#endif
        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            Leave();
            StopCapture();
            StopPlayback();

            GC.SuppressFinalize(this);
        }
    }
}
