using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sea
{
    // =============================================================
    // SEA · 音量滑杆: 点一下即跳转 / 按住拖动连续调
    //   用法: 在一条 Image 底轨上 AddComponent<SeaVolumeBar>(),
    //   再 Setup(fillImage, 初值, 回调)。fill 用左右锚定的子图。
    // =============================================================
    public sealed class SeaVolumeBar : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        RectTransform _bar;
        Image _fill;
        Action<float> _onChanged;
        float _value = 0.5f;

        void Awake()
        {
            _bar = (RectTransform)transform;
        }

        public void Setup(Image fill, float start, Action<float> onChanged)
        {
            _fill = fill;
            _onChanged = onChanged;
            if (_bar == null) _bar = (RectTransform)transform;   // 组件可能建在未激活物体上(Awake 未跑)
            Set(start);
        }

        public void Set(float v)
        {
            _value = Mathf.Clamp01(v);
            Apply();
        }
        public float Value => _value;

        void Apply()
        {
            if (_fill == null) return;
            var fr = (RectTransform)_fill.transform;
            float w = _bar.rect.width;
            fr.sizeDelta = new Vector2(Mathf.Max(0f, w * _value), fr.sizeDelta.y);
        }

        public void OnPointerDown(PointerEventData e) => DragTo(e);
        public void OnDrag(PointerEventData e) => DragTo(e);

        void DragTo(PointerEventData e)
        {
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _bar, e.position, e.pressEventCamera, out var local))
            {
                float w = _bar.rect.width;
                float v = w > 0f ? Mathf.Clamp01((local.x - _bar.rect.xMin) / w) : 0f;
                Set(v);
                _onChanged?.Invoke(_value);
            }
        }
    }
}
