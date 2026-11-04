using System;

namespace BIG.Unity.Networking.Voice
{
    /// <summary> Flags carried by every voice packet. </summary>
    [Flags]
    public enum VoiceFlags : byte
    {
        None = 0,
        /// <summary> Spoken into a walkie-talkie — also played by radios on the same frequency. </summary>
        Radio = 1 << 0
    }

    /// <summary>
    /// One remote speaker's jitter buffer + decoder. Packets arrive out of order and with gaps (unreliable channel);
    /// playback starts once <c>JitterBufferMs</c> of audio is buffered, then produces one frame per 20 ms:
    /// in-order packet → decode, lost packet → rebuild from the next packet's FEC or conceal (PLC).
    /// A talk spurt ends when the buffer stays empty for a few frames — the next packet starts a new spurt.
    /// Main thread only.
    /// </summary>
    internal sealed class VoicePlayback
    {
        public delegate void FrameDecoded(float[] frame, VoiceFlags flags, byte frequency, float level);

        private const int SLOTS = 64;
        private const int MAX_CONCEALED_FRAMES = 3; // Empty buffer for this long = speaker stopped talking.
        private const int MAX_EXTRA_FRAMES = 6;     // Buffer grew this far past the target (clock drift) → skip a frame.

        private readonly byte[][] _data = new byte[SLOTS][];
        private readonly int[] _length = new int[SLOTS];
        private readonly ushort[] _sequence = new ushort[SLOTS];
        private readonly bool[] _filled = new bool[SLOTS];
        private readonly VoiceFlags[] _flags = new VoiceFlags[SLOTS];
        private readonly byte[] _frequency = new byte[SLOTS];
        private readonly byte[] _level = new byte[SLOTS];

        private readonly VoiceDecoder _decoder = new VoiceDecoder();
        private readonly float[] _frame = new float[VoiceFormat.FRAME_SAMPLES];
        private readonly FrameDecoded _onFrame;

        private bool _playing;
        private bool _hasNext;
        private ushort _next;
        private int _buffered;
        private int _concealed;
        private double _clock;
        private VoiceFlags _lastFlags;
        private byte _lastFrequency;

        /// <summary> A talk spurt is being played. </summary>
        public bool IsPlaying => _playing;

        public VoicePlayback(FrameDecoded onFrame)
        {
            _onFrame = onFrame;
            for (int i = 0; i < SLOTS; i++)
                _data[i] = new byte[VoiceFormat.MAX_PACKET_BYTES];
        }

        public void Push(ushort sequence, ReadOnlySpan<byte> packet, VoiceFlags flags, byte frequency, byte level)
        {
            if (packet.Length == 0 || packet.Length > VoiceFormat.MAX_PACKET_BYTES) return;

            if (_hasNext)
            {
                int ahead = Diff(sequence, _next);
                if (ahead < 0) return;                 // Too late — already played or concealed.
                if (ahead >= SLOTS) Stop();             // Far ahead (long stall / sender restart) — resync.
            }

            int slot = sequence % SLOTS;
            if (_filled[slot] && _sequence[slot] == sequence) return; // Duplicate.
            if (!_filled[slot]) _buffered++;

            packet.CopyTo(_data[slot]);
            _length[slot] = packet.Length;
            _sequence[slot] = sequence;
            _filled[slot] = true;
            _flags[slot] = flags;
            _frequency[slot] = frequency;
            _level[slot] = level;

            if (!_playing && (!_hasNext || Diff(sequence, _next) < 0))
            {
                _next = sequence; // Spurt starts at the earliest packet received so far.
                _hasNext = true;
            }
        }

        public void Tick(float deltaTime, int jitterBufferMs)
        {
            if (!_playing)
            {
                int targetFrames = Math.Max(1, jitterBufferMs / VoiceFormat.FRAME_MS);
                if (_buffered < targetFrames) return;
                _playing = true;
                _clock = 0;
            }

            _clock += deltaTime;
            while (_playing && _clock >= VoiceFormat.FRAME_SECONDS)
            {
                _clock -= VoiceFormat.FRAME_SECONDS;
                ProduceFrame();
            }

            int maxBuffered = jitterBufferMs / VoiceFormat.FRAME_MS + MAX_EXTRA_FRAMES;
            if (_playing && _buffered > maxBuffered)
            {
                // Sender clock runs slightly faster than ours — drop one frame to keep latency bounded.
                if (Has(_next))
                    Consume(_next % SLOTS);
                _next++;
            }
        }

        private void ProduceFrame()
        {
            int slot = _next % SLOTS;
            float level;

            if (Has(_next))
            {
                _decoder.Decode(_data[slot].AsSpan(0, _length[slot]), _frame);
                _lastFlags = _flags[slot];
                _lastFrequency = _frequency[slot];
                level = _level[slot] / 255f;
                Consume(slot);
                _concealed = 0;
            }
            else
            {
                if (_buffered == 0 && ++_concealed > MAX_CONCEALED_FRAMES)
                {
                    Stop();
                    return;
                }

                ushort following = (ushort)(_next + 1);
                if (Has(following))
                {
                    int nextSlot = following % SLOTS;
                    _decoder.DecodeFec(_data[nextSlot].AsSpan(0, _length[nextSlot]), _frame);
                }
                else
                {
                    _decoder.Conceal(_frame);
                }

                level = 0f;
            }

            _next++;
            _onFrame(_frame, _lastFlags, _lastFrequency, level);
        }

        private bool Has(ushort sequence)
        {
            int slot = sequence % SLOTS;
            return _filled[slot] && _sequence[slot] == sequence;
        }

        private void Consume(int slot)
        {
            if (!_filled[slot]) return;
            _filled[slot] = false;
            _buffered--;
        }

        private void Stop()
        {
            _playing = false;
            _hasNext = false;
            _concealed = 0;
            _buffered = 0;
            Array.Clear(_filled, 0, SLOTS);
            _decoder.Reset();
        }

        /// <summary> Signed distance a - b on the 16-bit wrapping sequence. </summary>
        private static int Diff(ushort a, ushort b) => (short)(a - b);
    }
}
