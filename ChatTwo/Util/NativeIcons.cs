using System.IO;
using System.Reflection;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ChatTwo.Util;

/// <summary>
/// 原生 UI 图标加载器：从嵌入资源读取 PNG 字节，通过 <see cref="ITextureProvider.CreateFromImageAsync"/>
/// 创建 <see cref="IDalamudTextureWrap"/>。
///
/// 资源命名：EmbeddedResource 按 <c>RootNamespace.相对路径</c> 嵌入——本项目 RootNamespace=ChatTwo
/// （AssemblyName=ChatTwoCN），实际资源名是 <c>ChatTwo.images.toolbar_*.png</c>。
/// ⚠️ 不能硬编码 ChatTwoCN 前缀：GetManifestResourceStream 找不到 → 返回 null → 按钮悄悄回退 FontAwesome
/// （用户实测"没看到变化"的根因）。这里按文件名后缀动态匹配，杜绝该问题。
/// </summary>
internal static class NativeIcons
{
    private static readonly Assembly Asm = typeof(NativeIcons).Assembly;
    private static ITextureProvider? _tp;
    private static bool _loaded;
    private static bool _loadFailed;

    private static IDalamudTextureWrap? _chatSearch;
    private static IDalamudTextureWrap? _searchGo;
    private static IDalamudTextureWrap? _players;
    private static IDalamudTextureWrap? _close;
    private static IDalamudTextureWrap? _gear;
    private static IDalamudTextureWrap? _funnel;
    private static IDalamudTextureWrap? _bubble;
    private static IDalamudTextureWrap? _plus;
    private static IDalamudTextureWrap? _leaf;
    private static IDalamudTextureWrap? _lockOpen;
    private static IDalamudTextureWrap? _lockClosed;

    /// <summary>🔍 打开聊天记录（放大镜）— 用户新素材 icon_09</summary>
    public static IDalamudTextureWrap? ChatSearch => EnsureLoaded(_chatSearch);

    /// <summary>🔍 聊天记录窗口"搜索"按钮 — 用户新素材 icon_34</summary>
    public static IDalamudTextureWrap? SearchGo => EnsureLoaded(_searchGo);

    /// <summary>👤 玩家筛选（人形头像）— 用户新素材 icon_01</summary>
    public static IDalamudTextureWrap? Players => EnsureLoaded(_players);

    /// <summary>× 关闭/隐藏/重置筛选（粗X）— 用户新素材 icon_24</summary>
    public static IDalamudTextureWrap? Close => EnsureLoaded(_close);

    /// <summary>⚙️ 设置（齿轮）— 用户新素材 icon_00</summary>
    public static IDalamudTextureWrap? Gear => EnsureLoaded(_gear);

    /// <summary>🔻 筛选日期（漏斗）— 用户新素材 icon_01（与玩家同图，用户决策）</summary>
    public static IDalamudTextureWrap? Funnel => EnsureLoaded(_funnel);

    /// <summary>💬 频道切换（聊天气泡）— 用户新素材 icon_05</summary>
    public static IDalamudTextureWrap? Bubble => EnsureLoaded(_bubble);

    /// <summary>➕ 添加 Tab（加号）— 用户新素材 icon_11</summary>
    public static IDalamudTextureWrap? Plus => EnsureLoaded(_plus);

    /// <summary>🌱 新人频道（双叶嫩芽）— 用户新素材 icon_14</summary>
    public static IDalamudTextureWrap? Leaf => EnsureLoaded(_leaf);

    /// <summary>🔓 开锁（锁梁断开）— actionbar_hr1 r3c3（用户提供素材）</summary>
    public static IDalamudTextureWrap? LockOpen => EnsureLoaded(_lockOpen);

    /// <summary>🔒 上锁（实心锁体+钥匙孔）— actionbar_hr1 r3c4（用户提供素材）</summary>
    public static IDalamudTextureWrap? LockClosed => EnsureLoaded(_lockClosed);

    private static T? EnsureLoaded<T>(T? value) where T : class
    {
        // ⚠️ 2026-08-17 修复：懒加载曾因 _tp 永远为 null 从未触发（Load 只在构造函数调用，
        // 但懒加载改版时把构造函数调用删了）→ wrap 全 null → 按钮全部回退 FontAwesome。
        // 这里兜底：未加载且未失败时，直接从 Plugin.TextureProvider 取一次。
        if (!_loaded && !_loadFailed && _tp == null)
            Load(Plugin.TextureProvider);
        else if (!_loaded && !_loadFailed)
            Load(_tp!);
        return value;
    }

    /// <summary>
    /// 必须在 <see cref="IPluginLog"/> 与 <see cref="ITextureProvider"/> 可用之后调用一次。
    /// 加载失败只记日志，不抛异常——按钮在缺图时回退到 FontAwesome。
    /// </summary>
    public static void Load(ITextureProvider textureProvider)
    {
        if (_loaded) return;
        _tp = textureProvider;
        try
        {
            _chatSearch = LoadOne("toolbar_chatsearch.png");
            _searchGo   = LoadOne("toolbar_searchgo.png");
            _players    = LoadOne("toolbar_players.png");
            _close      = LoadOne("toolbar_close.png");
            _gear       = LoadOne("toolbar_gear.png");
            _funnel     = LoadOne("toolbar_funnel.png");
            _bubble     = LoadOne("toolbar_bubble.png");
            _plus       = LoadOne("toolbar_plus.png");
            _leaf       = LoadOne("toolbar_leaf.png");
            _lockOpen    = LoadOne("toolbar_lock_open.png");
            _lockClosed  = LoadOne("toolbar_lock_closed.png");
            _loaded = true;
        }
        catch (System.Exception e)
        {
            _loadFailed = true;
            Plugin.Log.Warning($"[NativeIcons] failed to load: {e}");
        }
    }

    private static IDalamudTextureWrap? LoadOne(string fileName)
    {
        // 动态匹配资源名：不假设前缀（RootNamespace 可能 ≠ AssemblyName），
        // 按 ".images.<fileName>" 后缀在程序集资源里找，找不到则报出全部资源名便于排查。
        var resName = Asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".images." + fileName, StringComparison.OrdinalIgnoreCase));
        if (resName == null)
        {
            Plugin.Log.Warning($"[NativeIcons] manifest resource not found (suffix .images.{fileName})");
            Plugin.Log.Warning($"[NativeIcons] available: {string.Join(", ", Asm.GetManifestResourceNames())}");
            return null;
        }
        using var s = Asm.GetManifestResourceStream(resName);
        if (s == null)
        {
            Plugin.Log.Warning($"[NativeIcons] manifest resource stream null: {resName}");
            return null;
        }
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        // 同步加载路径：ImageSharp 解码 PNG → Rgba32 原始像素 → CreateFromRaw。
        // ⚠️ 不要用 CreateFromImageAsync + .Wait()：其 continuation 需回到主线程，
        // 在插件构造函数（主线程）里同步等待会死锁，图标永远加载不出来。
        // ⚠️ 2026-08-17 用户决策：保持原生 UI 原样，不做颜色处理（之前转白是为了
        // 在深色 ImGui 背景上可见，但用户要求"既然用原生图标就保持原生原样"）。
        using var image = Image.Load<Rgba32>(bytes);
        var raw = image.ImageToRaw();
        return _tp!.CreateFromRaw(RawImageSpecification.Rgba32(image.Width, image.Height), raw, resName);
    }

    /// <summary>Dispose 所有加载的纹理。插件关闭时调用。</summary>
    public static void DisposeAll()
    {
        _chatSearch?.Dispose();   _chatSearch = null;
        _searchGo?.Dispose();     _searchGo = null;
        _players?.Dispose();      _players = null;
        _close?.Dispose();        _close = null;
        _gear?.Dispose();         _gear = null;
        _funnel?.Dispose();       _funnel = null;
        _bubble?.Dispose();       _bubble = null;
        _plus?.Dispose();         _plus = null;
        _leaf?.Dispose();         _leaf = null;
        _lockOpen?.Dispose();     _lockOpen = null;
        _lockClosed?.Dispose();   _lockClosed = null;
    }
}