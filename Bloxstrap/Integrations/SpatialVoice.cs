using System.Numerics;

using NAudio.Wave;

namespace Bloxstrap.Integrations
{
    /// <summary>
    /// How loud and where a voice sounds, going by how far away the speaker is and which way your camera faces,
    /// roughly how Roblox's voice chat does it
    /// </summary>
    public static class SpatialVoice
    {
        // full volume up close, fading out to silent at MaxDistance (the server stops sending a bit past it)
        public const float MinDistance = 7;
        public const float MaxDistance = 80;

        public static float GetGain(float distance)
        {
            if (distance <= MinDistance)
                return 1;

            if (distance >= MaxDistance)
                return 0;

            return MathF.Pow(1 - (distance - MinDistance) / (MaxDistance - MinDistance), 1.5f);
        }

        /// <summary>
        /// -1 is fully left, 1 is fully right
        /// </summary>
        public static float GetPan(Vector3 listener, Vector3 listenerRight, Vector3 speaker)
        {
            var offset = speaker - listener;
            float length = offset.Length();

            if (length < 0.5f || listenerRight.LengthSquared() < 0.5f)
                return 0;

            // never quite all the way to one ear, which sounds odd on headphones
            return Math.Clamp(Vector3.Dot(offset / length, Vector3.Normalize(listenerRight)), -1, 1) * 0.8f;
        }
    }

    /// <summary>
    /// Turns one speaker's mono voice into stereo with a volume and pan, easing between changes so they don't click
    /// </summary>
    public class SpatialSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _mono = Array.Empty<float>();

        private float _gain = 0;
        private float _pan = 0;

        public volatile float TargetGain = 0;
        public volatile float TargetPan = 0;

        public WaveFormat WaveFormat { get; }

        public SpatialSampleProvider(ISampleProvider monoSource)
        {
            _source = monoSource;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(monoSource.WaveFormat.SampleRate, 2);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;

            if (_mono.Length < frames)
                _mono = new float[frames];

            int read = _source.Read(_mono, 0, frames);

            float startGain = _gain, endGain = TargetGain;
            float startPan = _pan, endPan = TargetPan;

            for (int i = 0; i < read; i++)
            {
                float t = (i + 1f) / read;
                float gain = startGain + (endGain - startGain) * t;
                float pan = startPan + (endPan - startPan) * t;

                // constant power panning
                float angle = (pan + 1) * MathF.PI / 4;
                float sample = _mono[i] * gain;

                buffer[offset + i * 2] = sample * MathF.Cos(angle);
                buffer[offset + i * 2 + 1] = sample * MathF.Sin(angle);
            }

            if (read > 0)
            {
                _gain = endGain;
                _pan = endPan;
            }

            return read * 2;
        }
    }
}
