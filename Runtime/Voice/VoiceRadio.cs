using System;
using System.Collections.Generic;
using UnityEngine;

namespace BIG.Unity.Networking.Voice
{
    /// <summary>
    /// A walkie-talkie receiver: plays radio transmissions on its <see cref="Frequency"/> through its
    /// <see cref="VoiceEmitter"/> (profile Radio — filtered, delayed, 3D at the radio). Works on a held item
    /// or one lying on the ground; anyone near it hears it.
    /// Transmitting is done by the speaker: set <see cref="VoiceSpeaker.RadioTransmitting"/> while the radio button is held.
    /// </summary>
    public sealed class VoiceRadio : MonoBehaviour
    {
        private static readonly List<VoiceRadio> Active = new List<VoiceRadio>();

        [SerializeField] private VoiceEmitter _speaker;
        [SerializeField, Range(1, 255)] private int _frequency = 1;
        [SerializeField] private bool _isOn = true;

        /// <summary> Player carrying the radio — their own transmissions don't come out of it. Defaults to the parent player. </summary>
        public NetworkPlayer Holder { get; set; }

        public int Frequency
        {
            get => _frequency;
            set => _frequency = Mathf.Clamp(value, 1, 255);
        }

        public bool IsOn
        {
            get => _isOn;
            set => _isOn = value;
        }

        /// <summary> A transmission is coming out of this radio right now. </summary>
        public bool IsReceiving => _speaker.IsActive;

        private void Awake()
        {
            Holder = GetComponentInParent<NetworkPlayer>();
        }

        private void OnEnable() => Active.Add(this);
        private void OnDisable() => Active.Remove(this);

        /// <summary> Plays a speaker's radio frame on every radio tuned to the frequency. </summary>
        internal static void Deliver(NetworkPlayer speaker, int frequency, ReadOnlySpan<float> frame, float gain)
        {
            for (int i = 0; i < Active.Count; i++)
            {
                var radio = Active[i];
                if (radio._isOn && radio._frequency == frequency && radio.Holder != speaker)
                    radio._speaker.Write(frame, gain);
            }
        }

        private void Reset()
        {
            _speaker = GetComponentInChildren<VoiceEmitter>();
        }
    }
}
