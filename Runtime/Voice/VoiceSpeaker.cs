using System;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace BIG.Unity.Networking.Voice
{
    /// <summary>
    /// A player's voice. Put it on the player prefab next to <see cref="NetworkPlayer"/>, with a
    /// <see cref="VoiceEmitter"/> (profile Proximity) on the head.
    /// <list type="bullet">
    /// <item>Owner: records the microphone (<see cref="MicrophoneInput"/> modes Off / PushToTalk / Open), encodes Opus,
    /// sends 20 ms frames to the host on the unreliable channel.</item>
    /// <item>Host: relays each frame to every other player (never back to the speaker) and raises <see cref="PlayerSpoke"/>.</item>
    /// <item>Everyone else: jitter buffer → decode → proximity emitter on the head; radio frames additionally to every
    /// <see cref="VoiceRadio"/> on the frequency (delayed, filtered).</item>
    /// </list>
    /// Walkie-talkie: the game sets <see cref="RadioTransmitting"/> / <see cref="RadioFrequency"/> while the player
    /// holds the radio button — the voice is then heard both directly nearby and through radios.
    /// </summary>
    [RequireComponent(typeof(NetworkPlayer))]
    public sealed class VoiceSpeaker : NetworkBehaviour
    {
        [SerializeField] private VoiceSettings _settings;
        [SerializeField, Tooltip("Proximity emitter on the head (profile Proximity).")]
        private VoiceEmitter _mouth;

        private NetworkPlayer _player;
        private VoiceCapture _capture;
        private VoicePlayback _playback;
        private ushort _sequence;
        private bool _preferencesLoaded;
        private float _volume = 1f;
        private bool _muted;
        private IUserData _userData;

        public NetworkPlayer Player => _player;

        /// <summary> Walkie-talkie button held — frames are flagged Radio (and sent even in PushToTalk mode). Owner only. </summary>
        public bool RadioTransmitting { get; set; }
        /// <summary> Channel the owner transmits on, 1..255. </summary>
        public int RadioFrequency { get; set; } = 1;

        /// <summary> Talking right now (local: transmitting, remote: a talk spurt is playing). </summary>
        public bool IsSpeaking => _capture?.IsTransmitting ?? _playback?.IsPlaying ?? false;
        /// <summary> 0..1 level of the last frame — for talk indicators / lip flap. </summary>
        public float Level { get; private set; }

        /// <summary>
        /// Local player only: transmission is blocked because the game window isn't focused
        /// (<see cref="VoiceSettings.TransmitOnlyWhenFocused"/>) — show it in the UI.
        /// </summary>
        public bool BlockedByFocus => _capture != null && _settings.TransmitOnlyWhenFocused && !Application.isFocused;

        /// <summary> Talking into the walkie-talkie right now (local: radio button, remote: flags of the received voice). </summary>
        public bool TalkingOnRadio => IsSpeaking && (_capture != null ? RadioTransmitting : _lastFrameRadio);

        private bool _lastFrameRadio;

        /// <summary> Local playback volume of this player, 0..2. Persisted per <see cref="PlayerId"/>. </summary>
        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Mathf.Clamp(value, 0f, 2f);
                if (_player.Id.IsValid) _userData.Set(VolumeKey(_player.Id), _volume);
            }
        }

        /// <summary> Locally mute this player. Persisted per <see cref="PlayerId"/>. </summary>
        public bool Muted
        {
            get => _muted;
            set
            {
                _muted = value;
                if (_player.Id.IsValid) _userData.Set(MutedKey(_player.Id), _muted);
            }
        }

        /// <summary>
        /// Every 20 ms frame of this player's voice as PCM (48 kHz mono, 960 samples; the array is reused — copy what you keep).
        /// Local player: what the microphone sends. Remote players: what was decoded (also while muted).
        /// Hook for recording voices (e.g. monsters imitating players).
        /// </summary>
        public event Action<VoiceSpeaker, float[], float> FrameAvailable;

        private void Awake()
        {
            _player = GetComponent<NetworkPlayer>();
            _userData = God.PrayFor<IUserData>();
        }

        public override void OnStartClient()
        {
            if (IsOwner)
                _capture = new VoiceCapture(God.PrayFor<MicrophoneInput>(), _settings);
            else
                _playback = new VoicePlayback(OnFrameDecoded);
        }

        public override void OnStopClient()
        {
            _capture?.Dispose();
            _capture = null;
            _playback = null;
        }

        private void Update()
        {
            if (!_preferencesLoaded && _player.Id.IsValid)
            {
                _preferencesLoaded = true;
                _volume = _userData.GetFloat(VolumeKey(_player.Id), 1f);
                _muted = _userData.GetBool(MutedKey(_player.Id), false);
            }

            if (_capture != null)
            {
                _capture.Tick(RadioTransmitting, !BlockedByFocus, OnFrameEncoded);
                Level = _capture.IsTransmitting ? _capture.Level : 0f;
            }
            else if (_playback != null)
            {
                _playback.Tick(Time.unscaledDeltaTime, _settings.JitterBufferMs);
                if (!_playback.IsPlaying) Level = 0f;
            }
        }

        #region Send (owner → host → others)

        private void OnFrameEncoded(byte[] packet, int length, float[] frame, float level)
        {
            var flags = RadioTransmitting ? VoiceFlags.Radio : VoiceFlags.None;
            // Serialized immediately by FishNet — the capture buffer can be reused right after.
            ServerVoice(new ArraySegment<byte>(packet, 0, length), _sequence++, (byte)flags, (byte)RadioFrequency, (byte)(level * 255f));
            FrameAvailable?.Invoke(this, frame, level);
        }

        [ServerRpc(RequireOwnership = true)]
        private void ServerVoice(ArraySegment<byte> data, ushort sequence, byte flags, byte frequency, byte level, Channel channel = Channel.Unreliable)
        {
            Events.Raise(new PlayerSpoke(_player.Id, level / 255f, transform.position, ((VoiceFlags)flags & VoiceFlags.Radio) != 0));

            foreach (var connection in ServerManager.Clients.Values)
            {
                if (connection == Owner || !connection.IsActive) continue;
                TargetVoice(connection, data, sequence, flags, frequency, level, Channel.Unreliable);
            }
        }

        [TargetRpc]
        private void TargetVoice(NetworkConnection target, ArraySegment<byte> data, ushort sequence, byte flags, byte frequency, byte level, Channel channel = Channel.Unreliable)
        {
            // The segment points into FishNet's receive buffer — Push copies it.
            _playback?.Push(sequence, data.AsSpan(), (VoiceFlags)flags, frequency, level);
        }

        #endregion

        private void OnFrameDecoded(float[] frame, VoiceFlags flags, byte frequency, float level)
        {
            Level = level;
            _lastFrameRadio = (flags & VoiceFlags.Radio) != 0;
            FrameAvailable?.Invoke(this, frame, level);

            if (_muted) return;

            _mouth.Write(frame, _volume);
            if ((flags & VoiceFlags.Radio) != 0)
                VoiceRadio.Deliver(_player, frequency, frame, _volume);
        }

        private static UserDataKey VolumeKey(PlayerId id) => new UserDataKey($"VoiceVolume.{id.Value}");
        private static UserDataKey MutedKey(PlayerId id) => new UserDataKey($"VoiceMuted.{id.Value}");
    }
}
