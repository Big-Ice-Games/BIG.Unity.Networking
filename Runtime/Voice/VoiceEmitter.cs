using System;
using UnityEngine;

namespace BIG.Unity.Networking.Voice
{
    /// <summary>
    /// Where a voice comes out: a 3D <see cref="AudioSource"/> playing a streamed PCM clip fed by
    /// <see cref="VoiceSpeaker"/> (proximity — on the speaker's head) or <see cref="VoiceRadio"/> (walkie-talkie speaker).
    /// Route it to your voice mixer group via the AudioSource's Output (e.g. Sonity's "voice" group).
    /// <para>
    /// Profile <see cref="VoiceEmitterProfile.Radio"/> runs the audio through a radio speaker model (band-pass, overdrive,
    /// hiss), adds squelch bursts at the start and end of a transmission and delays it by <c>RadioDelayMs</c>.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class VoiceEmitter : MonoBehaviour
    {
        private const int RING_SECONDS = 2;
        private const float SILENCE_TO_END_SECONDS = 0.12f; // No writes for this long = transmission ended.

        [SerializeField] private VoiceSettings _settings;
        [SerializeField] private VoiceEmitterProfile _profile = VoiceEmitterProfile.Proximity;

        private readonly object _lock = new object();
        private readonly float[] _ring = new float[VoiceFormat.SAMPLE_RATE * RING_SECONDS];
        private readonly float[] _scratch = new float[VoiceFormat.FRAME_SAMPLES];
        private int _read;
        private int _write;
        private int _count;

        private AudioSource _source;
        private AudioClip _clip;
        private RadioSpeakerModel _radio;
        private bool _active;
        private float _lastWriteTime;

        // Output diagnostics: Unity's audio thread pulling samples from the stream.
        private int _pullCount;
        private int _lastSeenPullCount;
        private float _lastPullTime;
        private const float PULL_STALL_SECONDS = 1f;

        public VoiceEmitterProfile Profile => _profile;

        /// <summary> True while a transmission is coming out of this emitter. </summary>
        public bool IsActive => _active;

        /// <summary> Audio waiting in the buffer for Unity to play, in milliseconds (diagnostics). </summary>
        public int BufferedMs
        {
            get { lock (_lock) return _count * 1000 / VoiceFormat.SAMPLE_RATE; }
        }

        /// <summary> Seconds since Unity's audio thread last pulled samples from this emitter (diagnostics). </summary>
        public float SecondsSinceAudioPull => Time.unscaledTime - _lastPullTime;

        /// <summary> Times the stream was (re)started — config changes and stall recoveries (diagnostics). </summary>
        public int StreamRestarts { get; private set; }

        private void Awake()
        {
            _source = GetComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = true;
            _source.spatialBlend = 1f;
            _source.rolloffMode = AudioRolloffMode.Logarithmic;
            _source.minDistance = _profile == VoiceEmitterProfile.Radio ? _settings.RadioMinDistance : _settings.ProximityMinDistance;
            _source.maxDistance = _profile == VoiceEmitterProfile.Radio ? _settings.RadioMaxDistance : _settings.ProximityMaxDistance;

            if (_profile == VoiceEmitterProfile.Radio)
                _radio = new RadioSpeakerModel(_settings);

            StartStream();
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
        }

        private void OnDestroy()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
            _source.Stop();
            Destroy(_clip);
        }

        /// <summary> The AudioSource voice comes out of (diagnostics: mixer group, playing state). </summary>
        public AudioSource Source => _source;

        private void StartStream()
        {
            // Streamed clip: Unity pulls samples from OnPcmRead (audio thread) and still spatializes them.
            _clip = AudioClip.Create($"Voice_{name}", VoiceFormat.SAMPLE_RATE, 1, VoiceFormat.SAMPLE_RATE, true, OnPcmRead);
            _source.clip = _clip;
            _source.Play();
            _lastPullTime = Time.unscaledTime;
        }

        private void RestartStream(string reason)
        {
            StreamRestarts++;
            this.Log($"[Voice] {Owner()}: {reason} — restarting stream (#{StreamRestarts}).", LogLevel.Warning);
            _source.Stop();
            Destroy(_clip);
            StartStream();
        }

        /// <summary>
        /// Output device changed (headphones plugged in, default device switched): Unity resets the audio system and
        /// stops sources — restart the stream so voice keeps coming out of the new device.
        /// </summary>
        private void OnAudioConfigurationChanged(bool deviceWasChanged) =>
            RestartStream($"audio configuration changed (device changed: {deviceWasChanged})");

        private string Owner() => transform.parent != null ? $"{transform.parent.name}/{name}" : name;

        /// <summary> Queue one decoded 20 ms frame (48 kHz mono). Main thread. </summary>
        public void Write(ReadOnlySpan<float> frame, float gain)
        {
            if (!_active)
            {
                _active = true;
                if (_profile == VoiceEmitterProfile.Radio)
                {
                    AppendSilence(_settings.RadioDelayMs * VoiceFormat.SAMPLE_RATE / 1000);
                    AppendSquelch();
                }
            }

            frame.CopyTo(_scratch);
            if (_radio != null)
                _radio.Process(_scratch, _settings);

            for (int i = 0; i < _scratch.Length; i++)
                _scratch[i] *= gain;

            Append(_scratch);
            _lastWriteTime = Time.unscaledTime;
        }

        private void Update()
        {
            // Self-heal: the AudioSource claims to play, but Unity's audio thread stopped pulling samples.
            int pulls = System.Threading.Volatile.Read(ref _pullCount);
            if (pulls != _lastSeenPullCount)
            {
                _lastSeenPullCount = pulls;
                _lastPullTime = Time.unscaledTime;
            }
            else if (Time.unscaledTime - _lastPullTime > PULL_STALL_SECONDS)
            {
                RestartStream($"Unity stopped pulling audio (source playing: {_source.isPlaying})");
            }

            if (_active && Time.unscaledTime - _lastWriteTime > SILENCE_TO_END_SECONDS)
            {
                _active = false;
                if (_profile == VoiceEmitterProfile.Radio)
                    AppendSquelch();
            }
        }

        private void AppendSquelch()
        {
            int samples = _settings.RadioSquelchMs * VoiceFormat.SAMPLE_RATE / 1000;
            while (samples > 0)
            {
                int chunk = Math.Min(samples, _scratch.Length);
                _radio.Squelch(_scratch.AsSpan(0, chunk), _settings);
                Append(_scratch.AsSpan(0, chunk));
                samples -= chunk;
            }
        }

        private void AppendSilence(int samples)
        {
            lock (_lock)
            {
                samples = Math.Min(samples, _ring.Length - _count);
                for (int i = 0; i < samples; i++)
                {
                    _ring[_write] = 0f;
                    _write = (_write + 1) % _ring.Length;
                }
                _count += samples;
            }
        }

        private void Append(ReadOnlySpan<float> samples)
        {
            lock (_lock)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    if (_count == _ring.Length)
                    {
                        // Overflow (stalled audio thread) — drop the oldest sample, keep latency bounded.
                        _read = (_read + 1) % _ring.Length;
                        _count--;
                    }

                    _ring[_write] = samples[i];
                    _write = (_write + 1) % _ring.Length;
                    _count++;
                }
            }
        }

        /// <summary> Audio thread. Missing data plays as silence. </summary>
        private void OnPcmRead(float[] data)
        {
            System.Threading.Interlocked.Increment(ref _pullCount);
            lock (_lock)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (_count > 0)
                    {
                        data[i] = _ring[_read];
                        _read = (_read + 1) % _ring.Length;
                        _count--;
                    }
                    else
                    {
                        data[i] = 0f;
                    }
                }
            }
        }

        /// <summary> Cheap walkie-talkie speaker: band-pass → overdrive → hiss. Main thread, stateful per emitter. </summary>
        private sealed class RadioSpeakerModel
        {
            private readonly Biquad _highPass = new Biquad();
            private readonly Biquad _lowPass = new Biquad();
            private readonly System.Random _random = new System.Random();
            private float _lowCut = -1f;
            private float _highCut = -1f;

            public RadioSpeakerModel(VoiceSettings settings) => Configure(settings);

            public void Process(Span<float> samples, VoiceSettings settings)
            {
                Configure(settings);
                float drive = settings.RadioDrive;
                float normalize = 1f / (float)Math.Tanh(drive);

                for (int i = 0; i < samples.Length; i++)
                {
                    float x = _lowPass.Process(_highPass.Process(samples[i]));
                    x = (float)Math.Tanh(x * drive) * normalize;
                    samples[i] = x + Noise() * settings.RadioNoise;
                }
            }

            /// <summary> Filtered noise burst with a fade-out envelope. </summary>
            public void Squelch(Span<float> samples, VoiceSettings settings)
            {
                Configure(settings);
                for (int i = 0; i < samples.Length; i++)
                {
                    float envelope = 1f - (float)i / samples.Length;
                    samples[i] = _lowPass.Process(_highPass.Process(Noise())) * settings.RadioSquelchVolume * envelope;
                }
            }

            private void Configure(VoiceSettings settings)
            {
                if (_lowCut == settings.RadioLowCutHz && _highCut == settings.RadioHighCutHz) return;
                _lowCut = settings.RadioLowCutHz;
                _highCut = settings.RadioHighCutHz;
                _highPass.SetHighPass(_lowCut, VoiceFormat.SAMPLE_RATE);
                _lowPass.SetLowPass(_highCut, VoiceFormat.SAMPLE_RATE);
            }

            private float Noise() => (float)(_random.NextDouble() * 2.0 - 1.0);
        }

        /// <summary> RBJ cookbook biquad, transposed direct form II. </summary>
        private sealed class Biquad
        {
            private const double Q = 0.7071;
            private float _b0, _b1, _b2, _a1, _a2;
            private float _z1, _z2;

            public void SetHighPass(float frequency, int sampleRate)
            {
                double w = 2 * Math.PI * frequency / sampleRate;
                double alpha = Math.Sin(w) / (2 * Q);
                double cos = Math.Cos(w);
                double a0 = 1 + alpha;
                Set((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
            }

            public void SetLowPass(float frequency, int sampleRate)
            {
                double w = 2 * Math.PI * frequency / sampleRate;
                double alpha = Math.Sin(w) / (2 * Q);
                double cos = Math.Cos(w);
                double a0 = 1 + alpha;
                Set((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
            }

            private void Set(double b0, double b1, double b2, double a1, double a2)
            {
                _b0 = (float)b0; _b1 = (float)b1; _b2 = (float)b2; _a1 = (float)a1; _a2 = (float)a2;
            }

            public float Process(float x)
            {
                float y = _b0 * x + _z1;
                _z1 = _b1 * x - _a1 * y + _z2;
                _z2 = _b2 * x - _a2 * y;
                return y;
            }
        }
    }

    public enum VoiceEmitterProfile
    {
        /// <summary> Clean voice, starts as soon as it arrives. On the speaker's head. </summary>
        Proximity,
        /// <summary> Walkie-talkie speaker: filtered, noisy, delayed, with squelch. On a radio. </summary>
        Radio
    }
}
