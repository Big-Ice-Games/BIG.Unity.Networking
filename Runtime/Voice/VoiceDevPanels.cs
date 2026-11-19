using System.Collections.Generic;
using BIG.Unity.Networking.DevTools;
using UnityEngine;

namespace BIG.Unity.Networking.Voice
{
    /// <summary> Registers the voice modules in the developer overlay (Voice, Microphone, Audio output). </summary>
    internal static class VoiceDevPanels
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            DevOverlay.Register(new VoicePanel());
            DevOverlay.Register(new MicrophonePanel());
            DevOverlay.Register(new AudioOutputPanel());
        }
    }

    /// <summary> Who's talking (proximity / radio), their levels, per-player mute. </summary>
    internal sealed class VoicePanel : DevPanel
    {
        public override string Title => "Voice";
        public override int Order => 100;
        public override bool IsAvailable => Session.IsInGame;

        public override string Badge
        {
            get
            {
                int speaking = 0;
                foreach (var player in Session.Players.All)
                    if (player.TryGetComponent<VoiceSpeaker>(out var speaker) && speaker.IsSpeaking) speaking++;
                return speaking > 0 ? $"{speaking} talking" : null;
            }
        }

        public override string BadgeStyle => DevUI.GOOD;

        public override void Build(DevUI ui)
        {
            var session = Session;

            ui.Text(() =>
            {
                var local = LocalSpeaker(session);
                if (local == null) return "No avatar — voice starts in the match.";
                if (local.BlockedByFocus) return "Not sending — game window not focused";
                return local.IsSpeaking ? (local.TalkingOnRadio ? "You: transmitting on the walkie-talkie" : "You: transmitting") : "You: silent";
            }, () =>
            {
                var local = LocalSpeaker(session);
                return local == null ? DevUI.MUTED : local.BlockedByFocus ? DevUI.WARN : local.IsSpeaking ? DevUI.GOOD : DevUI.MUTED;
            });

            ui.List(() => session.Players.All, player => player.Id.Value, (row, player) =>
            {
                if (!player.TryGetComponent<VoiceSpeaker>(out var speaker)) return;
                var line = row.Row();
                line.Text(() => $"{(speaker.IsSpeaking ? "●" : "○")} {player.DisplayName}{(player.IsLocal ? " (you)" : "")}" +
                                (speaker.TalkingOnRadio ? "  · radio" : ""),
                    () => speaker.IsSpeaking ? DevUI.GOOD : null);
                line.Bar(() => speaker.Level);
                if (!player.IsLocal)
                    line.Button(() => speaker.Muted ? "Unmute" : "Mute", () => speaker.Muted = !speaker.Muted);
            });
        }

        private static VoiceSpeaker LocalSpeaker(NetworkSession session) =>
            session.Players.Local != null && session.Players.Local.TryGetComponent<VoiceSpeaker>(out var speaker) ? speaker : null;
    }

    /// <summary> Mic mode and device, Auto's reasoning, data flow, "hear yourself", speak-to-chat warning. </summary>
    internal sealed class MicrophonePanel : DevPanel
    {
        private MicrophoneInput _microphone;
        private MicrophoneLoopbackTest _loopback;

        public override string Title => "Microphone";
        public override int Order => 110;
        public override string Badge => _microphone == null ? null : _microphone.Mode.ToString();
        public override string BadgeStyle => _microphone != null && _microphone.Mode == MicrophoneMode.Off ? DevUI.WARN : null;

        public override void Build(DevUI ui)
        {
            _microphone = God.PrayFor<MicrophoneInput>();
            var microphone = _microphone;

            var mode = ui.Row();
            mode.Text(() => $"Mode: {microphone.Mode}");
            mode.Button("Change", microphone.CycleMode);

            var device = ui.Row();
            device.Text(() => microphone.Device == MicrophoneInput.AUTO_DEVICE ? "Device: Auto" : "Device: selected");
            device.Button("Next mic", () => SelectNext(microphone));

            ui.Text(() => microphone.IsRecording
                ? $"{microphone.RecordingDevice}  ·  {microphone.SampleRate} Hz\n{microphone.AutoReason}"
                : "Not recording", () => DevUI.MUTED);

            ui.Section(() => microphone.IsRecording).Text(() => microphone.SecondsSinceData < 0.25f
                    ? "Data: flowing"
                    : $"Data: none for {microphone.SecondsSinceData:0.0} s (auto-reopen at 1.5 s)",
                () => microphone.SecondsSinceData < 0.25f ? DevUI.MUTED : DevUI.BAD);

            var level = ui.Row();
            level.Text("Level");
            level.Bar(() => microphone.IsRecording ? microphone.GetLevel() : 0f);

            ui.Button(() => Loopback.IsRunning ? "Stop hearing yourself" : "Hear yourself (1 s delay)", () => Loopback.Toggle());

            ui.Section(() => microphone.SpeakToChatRisk).Text(
                "Bluetooth headphones play the game but you talk into another mic. Headphones with speak-to-chat / " +
                "talk-through mute the game while you talk — turn that off in the headphones' (phone) app, or use the " +
                "headphones' mic (lower sound quality: Bluetooth switches to call mode).", DevUI.WARN);
        }

        private MicrophoneLoopbackTest Loopback
        {
            get
            {
                if (_loopback != null) return _loopback;
                // Own child — the loopback stream must not share an AudioSource with anything else.
                var go = new GameObject("MicrophoneLoopbackTest");
                go.transform.SetParent(Owner.transform, false);
                return _loopback = go.AddComponent<MicrophoneLoopbackTest>();
            }
        }

        /// <summary> Auto → each device → Auto. </summary>
        private static void SelectNext(MicrophoneInput microphone)
        {
            var devices = MicrophoneInput.AvailableDevices;
            int next = System.Array.IndexOf(devices, microphone.Device) + 1; // -1 (Auto) → first device.
            microphone.SetDevice(next >= devices.Length ? MicrophoneInput.AUTO_DEVICE : devices[next]);
        }
    }

    /// <summary> Output device, Unity's audio config, listener sanity, per-emitter stream health; test tone / reset. </summary>
    internal sealed class AudioOutputPanel : DevPanel
    {
        private float _nextDeviceRefresh;
        private string _device = "…";
        private string _unityAudio = "…";
        private AudioSource _toneSource;
        private AudioClip _toneClip;

        public override string Title => "Audio output";
        public override int Order => 120;
        public override bool CollapsedByDefault => true;

        public override void Refresh()
        {
            // WASAPI lookup is a handful of COM calls — every 2 s is plenty.
            if (Time.unscaledTime < _nextDeviceRefresh) return;
            _nextDeviceRefresh = Time.unscaledTime + 2f;
            _device = AudioOutput.GetDeviceName();
            _unityAudio = AudioOutput.GetUnityConfiguration();
        }

        public override void Build(DevUI ui)
        {
            AudioSettings.OnAudioConfigurationChanged += _ => _nextDeviceRefresh = 0f;

            ui.Text(() => $"Device: {_device}");
            ui.Text(() => $"Unity: {_unityAudio}", () => DevUI.MUTED);
            if (AudioOutput.CanOpenSystemOutputSettings)
                ui.Button("Change output (Windows sound settings)", AudioOutput.OpenSystemOutputSettings);

            var buttons = ui.Row();
            buttons.Button("Test tone", PlayTestTone);
            buttons.Button("Reset audio", () =>
            {
                // Re-initializes Unity's output (same config) — voice emitters restart on the config event.
                Debug.Log("[Audio] Manual AudioSettings.Reset.");
                AudioSettings.Reset(AudioSettings.GetConfiguration());
            });

            ui.Text(ListenerStatus, () => ListenerStatusStyle());

            ui.Heading("VOICE STREAMS");
            ui.List(RemoteEmitters, EmitterKey, (row, emitter) =>
            {
                var player = emitter.GetComponentInParent<NetworkPlayer>();
                string who = player != null ? player.DisplayName : emitter.transform.root.name;
                row.Text(() =>
                {
                    bool pulling = emitter.SecondsSinceAudioPull < 0.5f;
                    var listener = ActiveListener(out _);
                    float distance = listener != null ? Vector3.Distance(listener.transform.position, emitter.transform.position) : -1f;
                    return $"{who}/{emitter.name}: {(pulling ? "playing" : $"NOT pulled {emitter.SecondsSinceAudioPull:0.0}s")}" +
                           $"  ·  {emitter.BufferedMs} ms queued  ·  {distance:0.0} m" +
                           (emitter.StreamRestarts > 0 ? $"  ·  restarts {emitter.StreamRestarts}" : "");
                }, () => emitter.SecondsSinceAudioPull < 0.5f ? DevUI.MUTED : DevUI.BAD);
            }, "No voice emitters (no avatars yet).");
        }

        private static string EmitterKey(VoiceEmitter emitter)
        {
            var player = emitter.GetComponentInParent<NetworkPlayer>();
            return $"{(player != null ? player.Id.Value : emitter.transform.root.name)}/{emitter.name}";
        }

        /// <summary> Own voice is never played locally — only the others' emitters matter. </summary>
        private static IEnumerable<VoiceEmitter> RemoteEmitters()
        {
            foreach (var emitter in Object.FindObjectsByType<VoiceEmitter>())
            {
                var player = emitter.GetComponentInParent<NetworkPlayer>();
                if (player == null || !player.IsLocal) yield return emitter;
            }
        }

        private static AudioListener ActiveListener(out int enabledCount)
        {
            AudioListener active = null;
            enabledCount = 0;
            foreach (var listener in Object.FindObjectsByType<AudioListener>())
            {
                if (!listener.isActiveAndEnabled) continue;
                enabledCount++;
                active = listener;
            }
            return active;
        }

        private static string ListenerStatus()
        {
            var active = ActiveListener(out int count);
            if (count == 0) return "Listener: NONE — nothing is audible";
            if (count > 1) return $"Listener: {count} active listeners — Unity uses one at random";
            return $"Listener: {active.name}{(AudioListener.pause ? " (PAUSED)" : "")}" +
                   (AudioListener.volume < 1f ? $"  ·  volume {AudioListener.volume:0.##}" : "");
        }

        private static string ListenerStatusStyle()
        {
            ActiveListener(out int count);
            return count != 1 || AudioListener.pause || AudioListener.volume <= 0f ? DevUI.BAD : DevUI.MUTED;
        }

        /// <summary> Plain 2D beep — tells "no game audio at all" apart from "no voice". </summary>
        private void PlayTestTone()
        {
            if (_toneSource == null)
            {
                _toneSource = Owner.AddComponent<AudioSource>();
                _toneSource.spatialBlend = 0f;
                const int rate = 48000;
                var samples = new float[rate / 2];
                for (int i = 0; i < samples.Length; i++)
                {
                    float fade = Mathf.Min(1f, Mathf.Min(i, samples.Length - i) / 2400f);
                    samples[i] = 0.25f * fade * Mathf.Sin(2f * Mathf.PI * 660f * i / rate);
                }
                _toneClip = AudioClip.Create("TestTone", samples.Length, 1, rate, false);
                _toneClip.SetData(samples, 0);
            }

            _toneSource.PlayOneShot(_toneClip);
        }
    }
}
