using UnityEngine;
using UnityEngine.EventSystems;

namespace Sea
{
    // =============================================================
    // SEA · 航行日志滚动条"滑块"的拖拽脚本(顶层独立类, 便于运行时 AddComponent)
    //   只负责把指针拖动换算成日志翻页, 交给 SeaHud.OnLogThumbDrag
    // =============================================================
    public sealed class SeaLogThumb : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public SeaHud hud;

        public void OnBeginDrag(PointerEventData e) { }
        public void OnDrag(PointerEventData e)
        {
            if (hud != null) hud.OnLogThumbDrag(e);
        }
    }
}
