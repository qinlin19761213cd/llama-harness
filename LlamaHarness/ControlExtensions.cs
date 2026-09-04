using System.Windows.Forms;

namespace LlamaHarness;

/// <summary>
/// 控件资源治理扩展（P1-1 审计修复：M1/M2/M3 控件泄漏族）。
/// Controls.Clear() 只移除引用不 Dispose 子控件 → 每次重建行/卡片的容器会累积 GDI/控件对象；
/// DisposeChildren() 移除并释放，杜绝无界增长。
/// </summary>
public static class ControlExtensions
{
    /// <summary>移除并释放全部子控件（倒序遍历，避免索引位移）。用于无界刷新场景（每次重建行/卡片的容器）。
    /// 接收 Control.ControlCollection（含 TableLayoutControlCollection），调用点写法与 Controls.Clear() 一致。</summary>
    public static void DisposeChildren(this Control.ControlCollection controls)
    {
        for (int i = controls.Count - 1; i >= 0; i--)
        {
            var child = controls[i];
            controls.RemoveAt(i);
            child.Dispose();
        }
    }
}
