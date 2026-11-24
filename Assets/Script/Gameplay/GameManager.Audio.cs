using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Options;
using UnityEngine;
using YARG.Core.Audio;
using YARG.Gameplay.Player;
using YARG.Settings;

namespace YARG.Gameplay
{
    public partial class GameManager
    {
        private const double DEFAULT_VOLUME = 1.0;
        public class StemState
        {
            private SongStem _stem;
            public double Volume => GetVolumeSetting();
            public int Total;
            public int Audible;
            public int ReverbCount;
            public float WhammyPitch;

            public StemState(SongStem stem)
            {
                _stem = stem;
            }

            public double SetMute(bool muted, bool keepAudibleUntilAllMiss)
            {
                if (muted)
                {
                    --Audible;
                }
                else if (Audible < Total)
                {
                    ++Audible;
                }

                if (keepAudibleUntilAllMiss)
                {
                    return Audible > 0 ? Volume : 0;
                }

                return Volume * Audible / Total;
            }

            public bool SetReverb(bool reverb)
            {
                if (reverb)
                {
                    ++ReverbCount;
                }
                else if (ReverbCount > 0)
                {
                    --ReverbCount;
                }
                return ReverbCount > 0;
            }

            public float SetWhammyPitch(float percent)
            {
                // TODO: Would be nice to handle multiple inputs
                // but for now last one wins
                WhammyPitch = Mathf.Clamp01(percent);
                return WhammyPitch;
            }

            private double GetVolumeSetting()
            {
                return _stem switch
                {
                    SongStem.Guitar    => SettingsManager.Settings.GuitarVolume.Value,
                    SongStem.Rhythm    => SettingsManager.Settings.RhythmVolume.Value,
                    SongStem.Bass      => SettingsManager.Settings.BassVolume.Value,
                    SongStem.Keys      => SettingsManager.Settings.KeysVolume.Value,
                    SongStem.Drums1    => SettingsManager.Settings.DrumsVolume.Value,
                    SongStem.Drums2    => SettingsManager.Settings.DrumsVolume.Value,
                    SongStem.Drums3    => SettingsManager.Settings.DrumsVolume.Value,
                    SongStem.Drums4    => SettingsManager.Settings.DrumsVolume.Value,
                    SongStem.Vocals    => SettingsManager.Settings.VocalsVolume.Value,
                    SongStem.Song      => SettingsManager.Settings.SongVolume.Value,
                    SongStem.Crowd     => SettingsManager.Settings.CrowdVolume.Value,
                    SongStem.Sfx       => SettingsManager.Settings.SfxVolume.Value,
                    SongStem.DrumSfx   => SettingsManager.Settings.DrumSfxVolume.Value,
                    SongStem.Metronome => SettingsManager.Settings.MetronomeVolume.Value,
                    _                  => DEFAULT_VOLUME
                };
            }
        }

        // Vocals compressor state: current gain reduction in dB (smoothed by attack/release),
        // and the last volume sent to the mixer so it's only touched when it actually changes.
        private const double VOCALS_COMPRESSOR_EPSILON = 0.001;

        private double _vocalsGainReductionDb;
        private double _vocalsCompressorLevelDb = double.NegativeInfinity;
        private int    _vocalsCompressorMicCount;
        private double _vocalsCompressorAppliedVolume = -1;

        private readonly Dictionary<SongStem, StemState>        _stemStates = new();
        private          SongStem                               _backgroundStem;
        private          TweenerCore<double, double, NoOptions> _volumeTween;

        private void LoadAudio()
        {
            bool isReplay = GlobalVariables.State.IsReplay || GlobalVariables.State.PlayingWithReplay;
            bool censorAudio = (isReplay && ReplayInfo.CensorshipEnabled) ||
                (!isReplay && SettingsManager.Settings.CensorMatureContent.Value);

            _stemStates.Clear();
            _mixer = Song.LoadAudio(GlobalVariables.State.SongSpeed, DEFAULT_VOLUME, censorAudio);
            if (_mixer == null)
            {
                _loadState = LoadFailureState.Error;
                _loadFailureMessage = "Failed to load audio!";
                return;
            }

            _backgroundStem = SongStem.Song;
            foreach (var channel in _mixer.Channels)
            {
                _stemStates.TryAdd(channel.Stem, new StemState(channel.Stem));
            }

            _backgroundStem = _stemStates.Count > 1 ? SongStem.Song : _stemStates.First().Key;
        }

        /// <summary>
        /// Sidechain compressor on the vocal stem keyed by the singers' mics, like an OBS compressor
        /// with the mic as ducking source. Singing above the threshold pulls the song's vocals down by
        /// (level - threshold) * (1 - 1 / ratio) dB; attack and release smooth that reduction.
        /// </summary>
        private void UpdateVocalsCompressor()
        {
            var settings = SettingsManager.Settings;
            if (!settings.VocalsCompressor.Value || !_stemStates.ContainsKey(SongStem.Vocals))
            {
                if (_vocalsCompressorAppliedVolume >= 0)
                {
                    // Turned off mid-song: back to the plain vocals volume
                    _vocalsGainReductionDb = 0;
                    _vocalsCompressorAppliedVolume = -1;
                    GlobalAudioHandler.SetVolumeSetting(SongStem.Vocals, settings.VocalsVolume.Value);
                }
                return;
            }

            // Sidechain level: the loudest singer
            double level = double.NegativeInfinity;
            int micCount = 0;
            foreach (var player in _players)
            {
                if (player is VocalsPlayer vocalsPlayer && vocalsPlayer.HasLiveMic)
                {
                    micCount++;
                    level = Math.Max(level, vocalsPlayer.MicLevelDbfs);
                }
            }

            _vocalsCompressorLevelDb = level;
            _vocalsCompressorMicCount = micCount;

            double threshold = settings.VocalsCompressorThreshold.Value;
            double ratio = Math.Max(1, settings.VocalsCompressorRatio.Value);
            double targetReduction = level > threshold
                ? (level - threshold) * (1 - 1 / ratio)
                : 0;

            // Attack when the reduction grows, release when it shrinks (one-pole smoothing)
            double timeMs = targetReduction > _vocalsGainReductionDb
                ? settings.VocalsCompressorAttack.Value
                : settings.VocalsCompressorRelease.Value;
            double coefficient = Math.Exp(-Time.unscaledDeltaTime * 1000.0 / Math.Max(1, timeMs));
            _vocalsGainReductionDb = targetReduction + (_vocalsGainReductionDb - targetReduction) * coefficient;

            double gainDb = settings.VocalsCompressorOutputGain.Value - _vocalsGainReductionDb;
            double volume = Math.Clamp(settings.VocalsVolume.Value * Math.Pow(10, gainDb / 20), 0, 1);

            if (Math.Abs(volume - _vocalsCompressorAppliedVolume) >= VOCALS_COMPRESSOR_EPSILON)
            {
                _vocalsCompressorAppliedVolume = volume;
                GlobalAudioHandler.SetVolumeSetting(SongStem.Vocals, volume);
            }
        }

        public void ChangeStarPowerStatus(bool active)
        {
            StarPowerActivations += active ? 1 : -1;
            if (StarPowerActivations < 0)
                StarPowerActivations = 0;
        }

        public void ChangeStemMuteState(SongStem stem, bool muted, float duration = 0.0f)
        {
            var setting = SettingsManager.Settings.MuteOnMiss.Value;
            if (setting == AudioFxMode.Off
            || !_stemStates.TryGetValue(stem, out var state)
            || (setting == AudioFxMode.MultitrackOnly && stem == _backgroundStem))
            {
                return;
            }

            // The background stem keeps its own split: it stands in for players whose instrument
            // has no stem of its own, so one of them missing should not silence the whole song.
            bool keepAudibleUntilAllMiss = SettingsManager.Settings.MuteOnlyWhenAllPlayersMiss.Value
                && stem != _backgroundStem;

            double volume = state.SetMute(muted, keepAudibleUntilAllMiss);

            if (duration <= 0.0f)
            {
                GlobalAudioHandler.SetVolumeSetting(stem, volume);
                return;
            }

            if (_volumeTween == null || !_volumeTween.IsPlaying())
            {
                _volumeTween = DOTween.To(() => GlobalAudioHandler.GetVolumeSetting(stem),
                    x => GlobalAudioHandler.SetVolumeSetting(stem, x), volume, duration);
            }
            else
            {
                _volumeTween.ChangeEndValue(volume);
            }
        }

        public void ChangeStemReverbState(SongStem stem, bool reverb)
        {
            var setting = SettingsManager.Settings.UseStarpowerFx.Value;
            if (setting == AudioFxMode.Off)
            {
                return;
            }

            StemState state;
            while (!_stemStates.TryGetValue(stem, out state))
            {
                if (stem == _backgroundStem)
                {
                    return;
                }
                stem = _backgroundStem;
            }

            if (setting == AudioFxMode.MultitrackOnly && stem == _backgroundStem)
            {
                return;
            }

            bool reverbActive = state.SetReverb(reverb);
            GlobalAudioHandler.SetReverbSetting(stem, reverbActive);
        }

        public void ChangeStemWhammyPitch(SongStem stem, float percent)
        {
            // If Whammy FX is turned off, ignore.
            if (!SettingsManager.Settings.UseWhammyFx.Value)
            {
                return;
            }

            // If the specified stem is the same as the background stem,
            // ignore the request. This may be a chart without separate
            // stems for each instrument. In that scenario we don't want
            // to pitch bend because we'd be bending the entire track.
            if (stem == _backgroundStem)
            {
                return;
            }

            // If we can't get the state for the stem, bail.
            if (!_stemStates.TryGetValue(stem, out var state))
            {
                return;
            }

            // Set the pitch
            float percentActive = state.SetWhammyPitch(percent);
            GlobalAudioHandler.SetWhammyPitchSetting(stem, percentActive);
        }
    }
}
