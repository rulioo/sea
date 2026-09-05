using UnityEngine;
using UnityEngine.EventSystems;

namespace Sea
{
    // =============================================================
    // SEA · 行情行"选中"感应组件(顶层独立类, 便于运行时 AddComponent)
    //   挂在每行整条的透明感应带上; 点在空白/文字处 = 点选该行(金亮)
    //   买/卖按钮在感应带之上, 点它们由按钮自己接管(也会顺手选中本行)
    // =============================================================
    public sealed class SeaRowPick : MonoBehaviour, IPointerClickHandler
    {
        public SeaHud hud;
        public int index;

        public void OnPointerClick(PointerEventData e)
        {
            if (hud != null) hud.OnRowClicked(index);
        }
    }
}
