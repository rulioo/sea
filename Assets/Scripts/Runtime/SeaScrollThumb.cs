using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sea
{
    // =============================================================
    // SEA · 右侧卡片滚动条"滑块"的拖拽脚本(顶层独立类, 便于运行时 AddComponent)
    //   航行日志与舰队货仓共用同一份: 谁把那三个引用填上, 它就替谁翻页。
    //   拖动 = 直接改 ScrollRect.verticalNormalizedPosition, 不自己存状态 ——
    //   面板每帧都会照当前位置重摆滑块(见 SeaHud.UpdateThumb), 两边各存一份必然对不上。
    // =============================================================
    public sealed class SeaScrollThumb : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public ScrollRect scroll;
        public RectTransform track, thumb;

        public void OnBeginDrag(PointerEventData e) { }

        public void OnDrag(PointerEventData e)
        {
            if (scroll == null || track == null || thumb == null) return;
            // 指针换算到轨道本地坐标, 再把滑块**中心**夹在轨道内。
            //   夹中心而不是夹边缘: 否则拖到两头时滑块会有一半被推出轨道外。
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(track, e.position, null, out local);
            float H = track.rect.height, th = thumb.rect.height;
            if (H - th <= 1f) return;                  // 还没布局出来 / 滑块占满整条 → 没得拖
            float lo = -H * 0.5f + th * 0.5f, hi = H * 0.5f - th * 0.5f;
            scroll.verticalNormalizedPosition = (Mathf.Clamp(local.y, lo, hi) - lo) / (H - th);
        }
    }
}
