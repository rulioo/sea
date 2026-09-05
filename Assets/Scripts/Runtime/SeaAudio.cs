using System.Collections.Generic;
using UnityEngine;

namespace Sea
{
    // =============================================================
    // SEA · 音频管理(音乐 + 音效)
    //   音乐: Resources/bgm/*.wav(循环播放, 可在设置里换曲)
    //   音效: Resources/sound/*.mp3 (click/buy/popup/exit/save)
    //   音量/静音/当前曲目存 PlayerPrefs, 跨局记住。
    //   用法: SeaAudio.Ensure(任意transform) 取回单例; 或 SeaPlay/SeaSettings 直接 GetComponent。
    //   ※ 不再自动播曲: 由 SeaPlay 决定首曲 —— 启动首页固定 head.wav(PlayBgmByName),
    //     进游戏后播主题曲(PlayBgmDefault: 玩家在设置里亲手选过曲 → 用所选, 否则 bgm.wav)。
    // =============================================================
    public sealed class SeaAudio : MonoBehaviour
    {
        public static SeaAudio Instance { get; private set; }

        AudioSource _bgm, _sfx;
        AudioClip[] _tracks;
        readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();

        bool _mute;
        float _bgmVol = 0.7f, _sfxVol = 0.85f;
        int _track = 0;

        const string K_MUTE = "sea.audio.mute";
        const string K_BGM = "sea.audio.bgmv";
        const string K_SFX = "sea.audio.sfxv";
        const string K_TRK = "sea.audio.trk";

        public bool Muted => _mute;
        public int TrackIndex => _track;
        public int TrackCount => _tracks != null ? _tracks.Length : 0;

        // 把曲目文件短名换成界面话术(没有的就原样显示)
        static string NiceName(string raw)
        {
            switch (raw)
            {
                case "head": return "片头 · 港口";
                case "bgm": return "主题 · 远航";
                default: return raw;
            }
        }
        public string TrackLabel(int i)
        {
            if (_tracks == null || i < 0 || i >= _tracks.Length) return "";
            return NiceName(_tracks[i].name);
        }

        void Awake()
        {
            Instance = this;
            _bgm = gameObject.AddComponent<AudioSource>();
            _bgm.loop = true;
            _bgm.playOnAwake = false;
            _bgm.spatialBlend = 0f;
            _sfx = gameObject.AddComponent<AudioSource>();
            _sfx.loop = false;
            _sfx.playOnAwake = false;
            _sfx.spatialBlend = 0f;

            _tracks = Resources.LoadAll<AudioClip>("bgm");
            var clips = Resources.LoadAll<AudioClip>("sound");
            foreach (var c in clips)
                if (!_clips.ContainsKey(c.name)) _clips[c.name] = c;

            _mute = PlayerPrefs.GetInt(K_MUTE, 0) == 1;
            _bgmVol = Mathf.Clamp01(PlayerPrefs.GetFloat(K_BGM, 0.7f));
            _sfxVol = Mathf.Clamp01(PlayerPrefs.GetFloat(K_SFX, 0.85f));
            _track = Mathf.Clamp(PlayerPrefs.GetInt(K_TRK, 0), 0, Mathf.Max(0, TrackCount - 1));

            ApplyVolumes();
        }

        public static SeaAudio Ensure(Component host)
        {
            if (Instance != null) return Instance;
            var a = host.GetComponent<SeaAudio>();
            if (a == null) a = host.gameObject.AddComponent<SeaAudio>();
            return a;
        }

        // ---------- 音乐 ----------
        public void PlayBgmTrack(int i)
        {
            if (_tracks == null || _tracks.Length == 0) return;
            _track = Mathf.Clamp(i, 0, _tracks.Length - 1);
            PlayerPrefs.SetInt(K_TRK, _track);
            _bgm.clip = _tracks[_track];
            if (_bgm.clip != null)
            {
                if (!_bgm.isPlaying) _bgm.Play();
                ApplyVolumes();
            }
        }

        // 玩家是否在"音乐音效"里亲手点选过曲目(没选过 → 进游戏默认主题曲 bgm.wav)
        public bool TrackChosenByUser => PlayerPrefs.HasKey(K_TRK);

        int ClipIndex(string name)
        {
            if (_tracks == null) return -1;
            for (int i = 0; i < _tracks.Length; i++)
                if (_tracks[i] != null && _tracks[i].name == name) return i;
            return -1;
        }

        // 主页背景乐(如 head.wav): 只换当前正在播的曲, 不改"设置所选曲目"。
        public void PlayBgmByName(string name)
        {
            if (_tracks == null) return;
            int i = ClipIndex(name);
            if (i < 0) return;
            _bgm.clip = _tracks[i];
            if (!_bgm.isPlaying) _bgm.Play();
            ApplyVolumes();
        }

        // 离开主页进游戏: 玩家设置里选过曲 → 用所选; 没选过 → 主题曲(bgm.wav)。
        public void PlayBgmDefault()
        {
            int pick = _track;
            if (!TrackChosenByUser)
            {
                int theme = ClipIndex("bgm");
                if (theme >= 0) pick = theme;
            }
            PlayBgmTrack(pick);
        }

        public float BgmVolume => _bgmVol;
        public float SfxVolume => _sfxVol;

        public void SetBgmVolume(float v)
        {
            _bgmVol = Mathf.Clamp01(v);
            PlayerPrefs.SetFloat(K_BGM, _bgmVol);
            ApplyVolumes();
        }
        public void SetSfxVolume(float v)
        {
            _sfxVol = Mathf.Clamp01(v);
            PlayerPrefs.SetFloat(K_SFX, _sfxVol);
        }
        public void SetMuted(bool m)
        {
            _mute = m;
            PlayerPrefs.SetInt(K_MUTE, m ? 1 : 0);
            ApplyVolumes();
        }
        public void ToggleMute() => SetMuted(!_mute);

        void ApplyVolumes()
        {
            if (_bgm != null) _bgm.volume = _mute ? 0f : _bgmVol;
            if (_sfx != null) _sfx.volume = _mute ? 0f : _sfxVol;
        }

        // ---------- 音效 ----------
        void Play(string key)
        {
            if (_mute || _sfx == null) return;
            if (_clips.TryGetValue(key, out var c) && c != null)
                _sfx.PlayOneShot(c, _sfxVol);
        }
        public void SfxClick() => Play("click");
        public void SfxTrade() => Play("buy");
        public void SfxPopup() => Play("popup");
        public void SfxSave() => Play("save");
        public void SfxExit() => Play("exit");
    }
}
