using FFXIVClientStructs.FFXIV.Client.UI;

namespace ChatTwo.Util;

/// <summary>tab 交互音效统一（原生 SFX 常量；legacy/仿原生共用同一套）。</summary>
public static class TabSfx
{
    public const uint Switch = 1u; // tab 切换（原生频道切换音效）

    public static void PlaySwitch()
    {
        unsafe { UIGlobals.PlaySoundEffect(Switch); }
    }
}
