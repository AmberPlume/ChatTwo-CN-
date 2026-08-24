using FFXIVClientStructs.FFXIV.Client.UI;

namespace ChatTwo;

public sealed partial class Plugin
{
    public static bool IsNativeContextMenuVisible()
    {
        try
        {
            unsafe
            {
                var ctxAddon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ContextMenu");
                return ctxAddon != null && ctxAddon->IsVisible;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>二级菜单（AddonContextSub）是否可见（兜底复位用）。</summary>
    public static unsafe bool IsNativeSubContextMenuVisible()
    {
        try
        {
            var addon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("AddonContextSub");
            return addon != null && addon->IsVisible;
        }
        catch
        {
            return false;
        }
    }
}
