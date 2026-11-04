using System;

namespace BIG.Unity.Networking.Voice
{
    /// <summary>
    /// Local player's voice input: microphone frames → 48 kHz → transmit gate → Opus.
    /// <list type="bullet">
    /// <item>Mode <see cref="MicrophoneMode.Off"/>: the device isn't even recorded (MicrophoneInput) — nothing is sent, radio included.</item>
    /// <item><see cref="MicrophoneMode.PushToTalk"/>: sends while PTT or the radio button is held.</item>
    /// <item><see cref="MicrophoneMode.Open"/>: sends while the voice is above the VAD threshold (+ hangover), or the radio button is held.</item>
    /// </list>
    /// </summary>
    internal sealed class VoiceCapture : IDisposable
    {
        /// <summary> packet[0..length) is reused on the next frame — send it before returning. </summary>
        public delegate void FrameEncoded(byte[] packet, int length, float[] frame, float level);

        private readonly MicrophoneInput _microphone;
        private readonly MicrophoneReader _reader;
        private readonly VoiceSettings _settings;
        private readonly VoiceEncoder _encoder;
        private readonly LinearResampler _resampler = new LinearResampler(VoiceFormat.SAMPLE_RATE);

        private readonly float[] _frame = new float[VoiceFormat.FRAME_SAMPLES];
        private readonly float[] _pending = new float[VoiceFormat.FRAME_SAMPLES * 4];
        private readonly byte[] _packet = new byte[VoiceFormat.MAX_PACKET_BYTES];
        private float[] _micFrame = Array.Empty<float>();
        private int _pendingCount;
        private int _epoch = -1;
        private int _hangoverFrames;

        /// <summary> Input level of the last frame, 0..1 — for "you are talking" UI. </summary>
        public float Level { get; private set; }
        /// <summary> Last frame was sent. </summary>
        public bool IsTransmitting { get; private set; }

        public VoiceCapture(MicrophoneInput microphone, VoiceSettings settings)
        {
            _microphone = microphone;
            _settings = settings;
            _encoder = new VoiceEncoder(settings);
            _reader = microphone.OpenReader();
        }

        /// <summary>
        /// Call every frame. Invokes <paramref name="onEncoded"/> for every 20 ms frame to send.
        /// <paramref name="allowed"/> = false (e.g. window not focused): the microphone is still drained — so nothing stale
        /// is sent when transmission resumes — but no frame leaves the machine, whatever the mode or buttons say.
        /// </summary>
        public void Tick(bool radioPressed, bool allowed, FrameEncoded onEncoded)
        {
            int sampleRate = _microphone.SampleRate;
            if (sampleRate == 0)
            {
                Level = 0f;
                IsTransmitting = false;
                return;
            }

            if (_microphone.Epoch != _epoch)
            {
                // Recording (re)started — device change, recovery, mode back from Off.
                _epoch = _microphone.Epoch;
                _micFrame = new float[sampleRate / 50];
                _resampler.Reset(sampleRate);
                _pendingCount = 0;
            }

            _encoder.Apply(_settings);

            while (_reader.TryRead(_micFrame))
            {
                _pendingCount += _resampler.Process(_micFrame, _pending.AsSpan(_pendingCount));

                while (_pendingCount >= VoiceFormat.FRAME_SAMPLES)
                {
                    Array.Copy(_pending, _frame, VoiceFormat.FRAME_SAMPLES);
                    _pendingCount -= VoiceFormat.FRAME_SAMPLES;
                    Array.Copy(_pending, VoiceFormat.FRAME_SAMPLES, _pending, 0, _pendingCount);

                    ProcessFrame(radioPressed, allowed, onEncoded);
                }
            }
        }

        private void ProcessFrame(bool radioPressed, bool allowed, FrameEncoded onEncoded)
        {
            float rms = AudioLevel.Rms(_frame);
            Level = AudioLevel.Normalize(rms);

            bool voiceDetected = AudioLevel.ToDecibels(rms) >= _settings.VadThresholdDb;
            if (voiceDetected)
                _hangoverFrames = _settings.VadHangoverMs / VoiceFormat.FRAME_MS;
            else if (_hangoverFrames > 0)
                _hangoverFrames--;

            bool open = _microphone.Mode switch
            {
                MicrophoneMode.Open => voiceDetected || _hangoverFrames > 0,
                MicrophoneMode.PushToTalk => _microphone.PushToTalkPressed,
                _ => false
            };

            IsTransmitting = allowed && (open || radioPressed);
            if (!IsTransmitting) return;

            int length = _encoder.Encode(_frame, _packet);
            if (length > 0)
                onEncoded(_packet, length, _frame, Level);
        }

        public void Dispose() => _reader.Dispose();
    }
}
