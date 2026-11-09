using System.Net.WebSockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

using Bloxstrap.Fluxer;

namespace Bloxstrap.Integrations
{
    /// <summary>
    /// Voice chat through the FluxStrap voice server (VoiceServer/ in the repository), so Fluxer itself doesn't need
    /// to change. Everyone in the same game server shares a room. Every build can listen; dev builds can talk if the
    /// server accepts their dev key, which the server checks itself, so a modified public build still can't talk.
    /// It's proximity chat like Roblox's: each client reports where its character is (read from the client's camera,
    /// see Fluxer/CameraReader.cs), the server only passes voices to people nearby, and they fade with distance.
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

        /// <summary>
        /// The microphone being used, for showing on the mute button
        /// </summary>
        public string? MicrophoneName { get; private set; }

        public const string DefaultMicrophone = "Windows default";

        /// <summary>
        /// The wave-in device to record from. Microphones are saved by name, since their numbers shift when
        /// devices are plugged in or out. Settings from before that saved a number, which is used as a fallback.
        /// </summary>
        public static (int Device, string Name) ResolveMicrophone()
        {
            var names = GetMicrophoneNames();
            string saved = App.Settings.Prop.VoiceChatMicrophoneName;

            if (!String.IsNullOrEmpty(saved))
            {
                int index = names.IndexOf(saved);

                if (index >= 0)
                    return (index, saved);

                App.Logger.WriteLine("VoiceChat::ResolveMicrophone", $"{saved} isn't plugged in, using the Windows default");
                return (-1, DefaultMicrophone);
            }

            int number = App.Settings.Prop.VoiceChatMicrophone;
            return number >= 0 && number < names.Count ? (number, names[number]) : (-1, DefaultMicrophone);
        }

        public static List<string> GetMicrophoneNames()
        {
            var names = new List<string>();

            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                names.Add(WaveInEvent.GetCapabilities(i).ProductName);

            return names;
        }

        private const int SampleRate = 48000;
        private const int FrameSamples = SampleRate / 50; // 20 ms, what the server expects per message
        private const int MaxPacketBytes = 1275;

        private readonly ActivityWatcher _activityWatcher;
        private readonly CameraReader _camera;

        private readonly object _selfLock = new();
        private CameraReader.CameraState? _self;

        private readonly object _sessionLock = new();
        private CancellationTokenSource? _session;
        private Channel<(WebSocketMessageType Type, byte[] Data)>? _outgoing;

        private readonly MixingSampleProvider _mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2)) { ReadFully = true };
        private readonly VolumeSampleProvider _volume;
        private WaveOutEvent? _output;

        private readonly Dictionary<uint, Speaker> _speakers = new();
        private readonly HashSet<string> _announcedRooms = new();

        private bool _disposed = false;

        public string Status { get; private set; } = "Not in a game";

        /// <summary>
        /// Whether the server accepted our dev key for this game
        /// </summary>
        public bool CanTalk { get; private set; } = false;

        /// <summary>
        /// Whether we're sending voice right now
        /// </summary>
        public bool Transmitting { get; private set; } = false;

        /// <summary>
        /// The mute button. Nothing gets sent while it's on.
        /// </summary>
        public bool Muted { get; set; } = false;

        /// <summary>
        /// Short messages worth showing as a notification
        /// </summary>
        public event EventHandler<string>? OnNotice;

        private class Speaker
        {
            public string Name = "Someone";
            public bool Talking;
            public Vector3 Position;
            public float Level;
            public long LastAudio;
            public readonly IOpusDecoder Decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);
            public readonly BufferedWaveProvider Buffer = new(new WaveFormat(SampleRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true
            };
            public SpatialSampleProvider? Input;
        }

        public readonly record struct TalkingSpeaker(string Name, Vector3 Position, float Level);

        /// <summary>
        /// Who's audibly talking right now, where they are and how loud they sound to us (0 to 1)
        /// </summary>
        public List<TalkingSpeaker> GetTalkingSpeakers()
        {
            long now = Environment.TickCount64;

            lock (_speakers)
            {
                return _speakers.Values
                    .Where(x => now - x.LastAudio < 300 && x.Input is not null && x.Input.TargetGain > 0)
                    .Select(x => new TalkingSpeaker(x.Name, x.Position, x.Level))
                    .ToList();
            }
        }

        /// <summary>
        /// Our camera, for putting things over the game where other players are
        /// </summary>
        public CameraReader.CameraState? Camera
        {
            get
            {
                lock (_selfLock)
                    return _self;
            }
        }

        /// <summary>
        /// How loud we are while we're talking (0 to 1)
        /// </summary>
        public float OwnLevel { get; private set; }

        // speech sits around 0.02 to 0.2 rms, the square root spreads that across most of the range
        private static float GetLevel(ReadOnlySpan<short> samples)
        {
            if (samples.IsEmpty)
                return 0;

            double sum = 0;

            foreach (short sample in samples)
                sum += (double)sample * sample;

            double rms = Math.Sqrt(sum / samples.Length) / short.MaxValue;
            return (float)Math.Clamp(Math.Sqrt(rms) * 2.2, 0, 1);
        }

        public VoiceChat(ActivityWatcher activityWatcher, int gamePid)
        {
            _activityWatcher = activityWatcher;
            _camera = new CameraReader(gamePid);

            if (!_camera.Supported)
                App.Logger.WriteLine("VoiceChat", "This client's position can't be read, so voice chat stays off for it");

            _volume = new(_mixer) { Volume = Math.Clamp(App.Settings.Prop.VoiceChatVolume, 0, 200) / 100f };

            _activityWatcher.OnGameJoin += (_, _) => Join();
            _activityWatcher.OnGameLeave += (_, _) => Leave();
        }

        private static string ServerUrl => String.IsNullOrWhiteSpace(App.Settings.Prop.VoiceChatServer) ? DefaultServer : App.Settings.Prop.VoiceChatServer;

        /// <summary>
        /// Who's online, from the same server (wss://host/voice -> https://host/stats)
        /// </summary>
        public static Uri StatsUrl
        {
            get
            {
                var builder = new UriBuilder(ServerUrl) { Path = "/stats" };
                builder.Scheme = builder.Scheme == "ws" ? "http" : "https";
                return builder.Uri;
            }
        }

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

            // a new game means a new Workspace and camera
            _camera.Reset();

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
            Queue(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new { t = "hello", v = 2, room, user = userId, key }));

            StartPlayback();

            using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
            var positionTask = Task.Run(() => ReportPosition(connection.Token));

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
                connection.Cancel();
                await positionTask;

                _outgoing = null;
                outgoing.Writer.TryComplete();

                StopCapture();
                ClearSpeakers();
                CanTalk = false;

                lock (_selfLock)
                    _self = null;

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

                    CanTalk = canTalk;

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

        /// <summary>
        /// Tells the server where our character is a few times a second, and keeps our camera for panning voices
        /// </summary>
        private async Task ReportPosition(CancellationToken token)
        {
            var last = new Vector3(float.NaN);
            var lastSent = DateTime.MinValue;

            while (!token.IsCancellationRequested)
            {
                if (_camera.TryRead(out var state))
                {
                    lock (_selfLock)
                        _self = state;

                    // only when it moves (up to 10 times a second), plus now and then so the server never forgets us
                    var sinceSent = DateTime.Now - lastSent;

                    if (Vector3.DistanceSquared(state.Focus, last) > 0.25f && sinceSent > TimeSpan.FromMilliseconds(100) || sinceSent > TimeSpan.FromSeconds(2))
                    {
                        Queue(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new { t = "pos", x = state.Focus.X, y = state.Focus.Y, z = state.Focus.Z }));
                        last = state.Focus;
                        lastSent = DateTime.Now;
                    }
                }

                try
                {
                    // read often so things drawn over the game keep up with the camera
                    await Task.Delay(33, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
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
            var output = Interlocked.Exchange(ref _output, null);

            output?.Stop();
            output?.Dispose();
        }

        // [speaker id][speaker position x, y, z][opus frame]
        private void OnAudio(byte[] data, int length)
        {
            if (length <= 16)
                return;

            uint id = BitConverter.ToUInt32(data, 0);
            var position = new Vector3(BitConverter.ToSingle(data, 4), BitConverter.ToSingle(data, 8), BitConverter.ToSingle(data, 12));
            var pcm = new short[FrameSamples];

            CameraReader.CameraState? self;

            lock (_selfLock)
                self = _self;

            lock (_speakers)
            {
                var speaker = GetSpeaker(id);

                if (self is CameraReader.CameraState me && speaker.Input is not null)
                {
                    // distance from your character, direction from your camera
                    speaker.Input.TargetGain = SpatialVoice.GetGain(Vector3.Distance(me.Focus, position));
                    speaker.Input.TargetPan = SpatialVoice.GetPan(me.Position, me.Right, position);
                }

                int samples;

                try
                {
                    samples = speaker.Decoder.Decode(data.AsSpan(16, length - 16), pcm, FrameSamples, false);
                }
                catch (Exception)
                {
                    return; // a corrupt frame, drop it
                }

                if (samples <= 0)
                    return;

                speaker.Position = position;
                speaker.Level = GetLevel(pcm.AsSpan(0, samples)) * (speaker.Input?.TargetGain ?? 1);
                speaker.LastAudio = Environment.TickCount64;

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
                speaker.Input = new SpatialSampleProvider(speaker.Buffer.ToSampleProvider());
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
        private int _gateHold = 0;

        private void StartCapture()
        {
            const string LOG_IDENT = "VoiceChat::StartCapture";

            if (_input is not null)
                return;

            try
            {
                _encoder = CreateEncoder();

                var (device, name) = ResolveMicrophone();

                _input = new WaveInEvent
                {
                    DeviceNumber = device,
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = 20
                };

                _input.DataAvailable += OnMicrophoneData;
                _input.StartRecording();

                MicrophoneName = name;
                App.Logger.WriteLine(LOG_IDENT, $"Microphone on: {name} ({(App.Settings.Prop.VoiceChatOpenMic ? "open mic" : "push to talk")})");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                OnNotice?.Invoke(this, "Couldn't open your microphone. Check Windows' microphone privacy settings, or pick another one in FluxStrap.");
                StopCapture();
            }
        }

        // CELT only (restricted low delay): Concentus' SILK speech encoder can throw on some audio
        private static IOpusEncoder CreateEncoder()
        {
            var encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
            encoder.Bitrate = 40000;
            return encoder;
        }

        private void StopCapture()
        {
            // leaving a game and closing it can both get here at once
            var input = Interlocked.Exchange(ref _input, null);

            if (input is null)
                return;

            input.DataAvailable -= OnMicrophoneData;

            try
            {
                input.StopRecording();
            }
            catch (Exception)
            {
                // already stopped
            }

            input.Dispose();
            Transmitting = false;
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
            float level = GetLevel(_frame);
            bool transmit = ShouldTransmit(level);

            OwnLevel = transmit ? level : 0;

            if (transmit != Transmitting)
            {
                Transmitting = transmit;
                Queue(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new { t = "talk", on = transmit }));
            }

            if (!transmit || _encoder is null)
                return;

            var packet = new byte[MaxPacketBytes];
            int length;

            try
            {
                length = _encoder.Encode(_frame, FrameSamples, packet, packet.Length);
            }
            catch (Exception ex)
            {
                // this runs on the recording thread, where an exception would stop the microphone for good
                App.Logger.WriteLine("VoiceChat::OnFrame", $"Couldn't encode a frame ({ex.GetType().Name}), starting the encoder over");
                _encoder = CreateEncoder();
                return;
            }

            if (length > 0)
                Queue(WebSocketMessageType.Binary, packet[..length]);
        }

        private bool ShouldTransmit(float level)
        {
            if (Muted)
                return false;

            if (!App.Settings.Prop.VoiceChatOpenMic)
                return (GetAsyncKeyState(App.Settings.Prop.VoiceChatPushToTalkKey) & 0x8000) != 0 && IsClientFocused();

            // open mic: a simple noise gate that stays open for 300 ms after you stop talking (0.27 is an rms of about 0.015)
            if (level > 0.27f)
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
            _camera.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
