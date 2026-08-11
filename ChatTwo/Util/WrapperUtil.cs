using System.Diagnostics;
using ChatTwo.Resources;
using Dalamud.Interface.ImGuiNotification;

namespace ChatTwo.Util;

public static class WrapperUtil
{
    public static void AddNotification(string content, NotificationType type, bool minimized = true)
    {
        Plugin.Notification.AddNotification(new Notification { Content = content, Type = type, Minimized = minimized });
    }

    public static void TryOpenUri(Uri uri)
    {
        TryOpenUri(uri.ToString());
    }

    /// <summary>
    /// 直接以原始字符串打开 URL（和 DR / OmenTools 的 Util.OpenLink 行为一致）。
    /// 使用 Process.Start + UseShellExecute，不经过 Uri 类的强制编码/转义，
    /// 适用于贴吧/FFLogs 等要求特定编码格式的站点。
    /// </summary>
    public static void TryOpenUri(string rawUrl)
    {
        try
        {
            Plugin.Log.Debug($"Opening URL {rawUrl} in default browser");
            Process.Start(new ProcessStartInfo(rawUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error opening URL: {ex}");
            AddNotification(Language.Context_OpenInBrowserError, NotificationType.Error);
        }
    }
}