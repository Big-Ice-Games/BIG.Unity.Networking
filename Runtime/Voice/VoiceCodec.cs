using System;
using Concentus;
using Concentus.Enums;

namespace BIG.Unity.Networking.Voice
{
    /// <summary> Wire format constants: 48 kHz mono, 20 ms Opus frames. </summary>
    public static class VoiceFormat
    {
        public const int SAMPLE_RATE = 48000;
        public const int FRAME_MS = 20;
        public const int FRAME_SAMPLES = SAMPLE_RATE * FRAME_MS / 1000; // 960
        public const float FRAME_SECONDS = FRAME_MS / 1000f;
        /// <summary> Upper bound of one encoded frame (24 kbps × 20 ms ≈ 60 B; generous for higher bitrates). </summary>
        public const int MAX_PACKET_BYTES = 400;
    }

    /// <summary> Opus encoder (managed Concentus — same code on every platform, no native binaries). </summary>
    internal sealed class VoiceEncoder
    {
        private readonly IOpusEncoder _encoder;

        public VoiceEncoder(VoiceSettings settings)
        {
            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            _encoder = OpusCodecFactory.CreateEncoder(VoiceFormat.SAMPLE_RATE, 1, OpusApplication.OPUS_APPLICATION_VOIP, null);
            _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            _encoder.UseInbandFEC = true;
            _encoder.UseDTX = false; // We gate transmission ourselves (PTT / VAD).
            Apply(settings);
        }

        public void Apply(VoiceSettings settings)
        {
            _encoder.Bitrate = settings.Bitrate;
            _encoder.Complexity = settings.Complexity;
            _encoder.PacketLossPercent = settings.ExpectedPacketLossPercent;
        }

        /// <summary> Encodes one <see cref="VoiceFormat.FRAME_SAMPLES"/> frame, returns the packet length. </summary>
        public int Encode(ReadOnlySpan<float> frame, Span<byte> packet) =>
            _encoder.Encode(frame, VoiceFormat.FRAME_SAMPLES, packet, packet.Length);
    }

    /// <summary> Opus decoder with packet loss concealment and FEC recovery. One per remote speaker. </summary>
    internal sealed class VoiceDecoder
    {
        private readonly IOpusDecoder _decoder;

        public VoiceDecoder()
        {
            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            _decoder = OpusCodecFactory.CreateDecoder(VoiceFormat.SAMPLE_RATE, 1, null);
        }

        public void Decode(ReadOnlySpan<byte> packet, Span<float> frame) =>
            _decoder.Decode(packet, frame, VoiceFormat.FRAME_SAMPLES, false);

        /// <summary> Lost frame rebuilt from the FEC data carried by the NEXT packet. </summary>
        public void DecodeFec(ReadOnlySpan<byte> nextPacket, Span<float> frame) =>
            _decoder.Decode(nextPacket, frame, VoiceFormat.FRAME_SAMPLES, true);

        /// <summary> Lost frame with nothing to rebuild from — Opus extrapolates (packet loss concealment). </summary>
        public void Conceal(Span<float> frame) =>
            _decoder.Decode(ReadOnlySpan<byte>.Empty, frame, VoiceFormat.FRAME_SAMPLES, false);

        public void Reset() => _decoder.ResetState();
    }

    /// <summary>
    /// Streaming linear resampler (mono). Microphones usually run at 48 kHz already — then it's a straight copy.
    /// Linear interpolation is fine for voice going into a 24 kbps codec.
    /// </summary>
    internal sealed class LinearResampler
    {
        private int _inRate;
        private double _position; // Fractional read position relative to the current input block.
        private float _last;      // Last sample of the previous block (interpolation across blocks).

        public LinearResampler(int inRate) => Reset(inRate);

        public void Reset(int inRate)
        {
            _inRate = inRate;
            _position = 0;
            _last = 0f;
        }

        /// <summary>
        /// Resamples input to <see cref="VoiceFormat.SAMPLE_RATE"/> and appends to output; returns samples written.
        /// </summary>
        public int Process(ReadOnlySpan<float> input, Span<float> output)
        {
            if (input.Length == 0) return 0;

            if (_inRate == VoiceFormat.SAMPLE_RATE)
            {
                int count = Math.Min(input.Length, output.Length);
                input.Slice(0, count).CopyTo(output);
                return count;
            }

            double step = (double)_inRate / VoiceFormat.SAMPLE_RATE;
            int written = 0;
            // Index -1 = _last (previous block), 0..n-1 = input.
            while (written < output.Length)
            {
                int index = (int)Math.Floor(_position);
                if (index + 1 >= input.Length) break;

                float a = index < 0 ? _last : input[index];
                float b = input[index + 1];
                float t = (float)(_position - index);
                output[written++] = a + (b - a) * t;
                _position += step;
            }

            _position -= input.Length;
            _last = input[input.Length - 1];
            return written;
        }
    }
}
