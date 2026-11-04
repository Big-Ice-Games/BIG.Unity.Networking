using UnityEngine;

namespace BIG.Unity.Networking.Voice
{
    /// <summary>
    /// All voice tuning in one asset (Create > BIG > Networking > Voice Settings), referenced by
    /// <see cref="VoiceSpeaker"/> and <see cref="VoiceEmitter"/>. Change values in play mode to tune live.
    /// </summary>
    [CreateAssetMenu(fileName = "VoiceSettings", menuName = "BIG/Networking/Voice Settings")]
    public sealed class VoiceSettings : ScriptableObject
    {
        [Header("Codec (Opus)")]
        [Tooltip("Bits per second per speaker. 16-32k is plenty for voice.")]
        [Range(8000, 64000)] public int Bitrate = 24000;
        [Tooltip("Encoder CPU/quality trade-off 0-10.")]
        [Range(0, 10)] public int Complexity = 5;
        [Tooltip("Expected packet loss — feeds Opus in-band FEC (lost frames reconstructed from the next packet).")]
        [Range(0, 50)] public int ExpectedPacketLossPercent = 10;

        [Header("Privacy")]
        [Tooltip("Never transmit while the game window is not focused (player alt-tabbed to talk to someone in the room). " +
                 "Incoming voice keeps playing. Also stops a push-to-talk key that got 'stuck' by alt-tabbing while held.")]
        public bool TransmitOnlyWhenFocused = true;

        [Header("Open microphone (voice activity detection)")]
        [Tooltip("Frames louder than this open the gate. Raise if background noise gets transmitted.")]
        [Range(-80f, 0f)] public float VadThresholdDb = -45f;
        [Tooltip("Keep transmitting this long after the voice drops below the threshold (no clipped word endings).")]
        [Range(0, 1000)] public int VadHangoverMs = 300;

        [Header("Playback")]
        [Tooltip("Received audio buffered before playback starts — absorbs network jitter. Lower = less latency, more dropouts.")]
        [Range(20, 300)] public int JitterBufferMs = 60;

        [Header("Proximity voice (AudioSource on the speaker's head)")]
        public float ProximityMinDistance = 1.5f;
        public float ProximityMaxDistance = 25f;

        [Header("Walkie-talkie")]
        [Tooltip("Extra delay of the radio copy — the direct voice nearby is heard first.")]
        [Range(0, 1000)] public int RadioDelayMs = 220;
        [Tooltip("Band-pass of the radio speaker.")]
        public float RadioLowCutHz = 350f;
        public float RadioHighCutHz = 3200f;
        [Tooltip("Overdrive before soft clipping — the 'cheap speaker' crunch.")]
        [Range(1f, 10f)] public float RadioDrive = 3f;
        [Tooltip("Hiss level under the transmission.")]
        [Range(0f, 0.2f)] public float RadioNoise = 0.025f;
        [Tooltip("Noise burst at the start and end of each transmission (squelch).")]
        [Range(0, 300)] public int RadioSquelchMs = 90;
        [Range(0f, 1f)] public float RadioSquelchVolume = 0.25f;
        public float RadioMinDistance = 0.5f;
        public float RadioMaxDistance = 12f;
    }
}
