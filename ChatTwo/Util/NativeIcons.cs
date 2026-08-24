using System.IO;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ChatTwo.Util;

/// <summary>
/// 原生 UI 图标加载器：从嵌入资源读取 PNG 字节，通过 <see cref="ITextureProvider.CreateFromImageAsync"/>
/// 创建 <see cref="IDalamudTextureWrap"/>。
/// /// 资源命名：EmbeddedResource 按 <c>RootNamespace.相对路径</c> 嵌入——本项目 RootNamespace=ChatTwo
/// （AssemblyName=ChatTwoCN），实际资源名是 <c>ChatTwo.images.toolbar_*.png</c>。
/// 不能硬编码 ChatTwoCN 前缀：GetManifestResourceStream 找不到 → 返回 null → 按钮悄悄回退 FontAwesome。
/// 按文件名后缀动态匹配，杜绝该问题。
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
    private static IDalamudTextureWrap? _tabCapLeft;
    private static IDalamudTextureWrap? _tabMiddle;
    private static IDalamudTextureWrap? _tabCapRight;
    private static IDalamudTextureWrap? _tabDivider;
    private static IDalamudTextureWrap? _tabIndicator;
    private static IDalamudTextureWrap? _resizeHandleNormal;
    private static IDalamudTextureWrap? _resizeHandleHighlighted;

    /// <summary>[搜索] 打开聊天记录（放大镜）— 新素材 icon_09</summary>
    public static IDalamudTextureWrap? ChatSearch => EnsureLoaded(_chatSearch);

    /// <summary>[搜索] 聊天记录窗口"搜索"按钮 — 新素材 icon_34</summary>
    public static IDalamudTextureWrap? SearchGo => EnsureLoaded(_searchGo);

    /// <summary>[玩家] 玩家筛选（人形头像）— 新素材 icon_01</summary>
    public static IDalamudTextureWrap? Players => EnsureLoaded(_players);

    /// <summary>× 关闭/隐藏/重置筛选（粗X）— 新素材 icon_24</summary>
    public static IDalamudTextureWrap? Close => EnsureLoaded(_close);

    /// <summary>[设置] 设置（齿轮）— 新素材 icon_00</summary>
    public static IDalamudTextureWrap? Gear => EnsureLoaded(_gear);

    /// <summary>[筛选] 筛选日期（漏斗）— 新素材 icon_01（与玩家同图）</summary>
    public static IDalamudTextureWrap? Funnel => EnsureLoaded(_funnel);

    /// <summary>[频道] 频道切换（聊天气泡）— 新素材 icon_05</summary>
    public static IDalamudTextureWrap? Bubble => EnsureLoaded(_bubble);

    /// <summary>[加号] 添加 Tab（加号）— 新素材 icon_11</summary>
    public static IDalamudTextureWrap? Plus => EnsureLoaded(_plus);

    /// <summary>[新人] 新人频道（双叶嫩芽）— 新素材 icon_14</summary>
    public static IDalamudTextureWrap? Leaf => EnsureLoaded(_leaf);

    /// <summary>[解锁] 开锁（锁梁断开）— actionbar_hr1 r3c3（提供素材）</summary>

    /// <summary>[锁定] 上锁（实心锁体+钥匙孔）— actionbar_hr1 r3c4（提供素材）</summary>

    // 底部 tab 栏三段式（提供 chatlog_extracted）
    /// <summary>tab 栏最左侧装饰左帽（可拖动聊天框）43x50</summary>
    public static IDalamudTextureWrap? TabCapLeft => EnsureLoaded(_tabCapLeft);
    /// <summary>tab 栏中间真正的 tab（写名称）56x51，每加一个 tab 拼一个</summary>
    public static IDalamudTextureWrap? TabMiddle => EnsureLoaded(_tabMiddle);
    /// <summary>tab 栏最右侧右帽 40x48</summary>
    public static IDalamudTextureWrap? TabCapRight => EnsureLoaded(_tabCapRight);
    /// <summary>tab 之间分割线 6x48（左帽、分割线、中段、分割线、中段…右帽）</summary>
    public static IDalamudTextureWrap? TabDivider => EnsureLoaded(_tabDivider);
    /// <summary>选中指示点（金色）21x21，选中 tab 左上角</summary>
    public static IDalamudTextureWrap? TabIndicator => EnsureLoaded(_tabIndicator);

    /// <summary>缩放手柄常态 31x31（替换三窗口金字塔手柄）</summary>
    public static IDalamudTextureWrap? ResizeHandleNormal => EnsureLoaded(_resizeHandleNormal);
    /// <summary>缩放手柄高亮/被选中态 42x42（图形内容 31x31 居中，周围 6px 透明边）</summary>
    public static IDalamudTextureWrap? ResizeHandleHighlighted => EnsureLoaded(_resizeHandleHighlighted);

    /// <summary>绘制缩放手柄：常态/高亮两图按"图形内容"统一尺寸（UV 裁剪内容 bbox），
    /// 否则高亮图 42x42 拉伸后内容比常态小。</summary>
    public static void DrawResizeHandle(ImDrawListPtr dl, Vector2 pos, Vector2 size, bool highlighted)
    {
        var wrap = highlighted ? ResizeHandleHighlighted : ResizeHandleNormal;
        if (wrap == null)
            return;
        // 高亮图图形内容 bbox = (6,6)-(36,36) 归一化 UV；常态占满
        var uv0 = highlighted ? new Vector2(6f / 42f, 6f / 42f) : Vector2.Zero;
        var uv1 = highlighted ? new Vector2(36f / 42f, 36f / 42f) : Vector2.One;
        dl.AddImage(wrap.Handle, pos, pos + size, uv0, uv1);
    }

    // 统一（三窗口 × 绘制/hit-test/置顶共 9 处之前各写魔法数字，
    // 改过 4 次尺寸/位置——抽成统一方法防漏改）。
    /// <summary>缩放手柄尺寸（10px × UI scale）</summary>
    public static float ResizeHandleSize() => 10f * ImGuiHelpers.GlobalScale;

    /// <summary>缩放手柄 X 内缩：默认 3px / 仿原生 8px（落在消息区背景内）</summary>
    public static float ResizeHandleInsetX(bool nativeBg) => nativeBg ? 8f : 3f;

    /// <summary>缩放手柄 Y 内缩：默认 3px / 仿原生 4px + 下移 5px</summary>
    public static float ResizeHandleInsetY(bool nativeBg) => (nativeBg ? 4f : 3f) + 5f;

    private static T? EnsureLoaded<T>(T? value) where T : class
    {
        // _tp 永远为 null 从未触发（Load 只在构造函数调用，
        // 但懒加载重构时把构造函数调用删了）→ wrap 全 null → 按钮全部回退 FontAwesome。
        // ，直接从 Plugin.TextureProvider 取一次。
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
            _tabCapLeft  = LoadOne("toolbar_tab_cap_left.png");
            _tabMiddle   = LoadOne("toolbar_tab_middle.png");
            _tabCapRight = LoadOne("toolbar_tab_cap_right.png");
            _tabDivider  = LoadOne("toolbar_tab_divider.png");
            _tabIndicator = LoadOne("toolbar_tab_indicator.png");
            _resizeHandleNormal = LoadOne("toolbar_resize_handle_normal.png");
            _resizeHandleHighlighted = LoadOne("toolbar_resize_handle_highlighted.png");
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
        // CreateFromImageAsync + .Wait：其 continuation 需回到主线程，
        // 在插件构造函数（主线程）里同步等待会死锁，图标永远加载不出来。
        // 保持原生 UI 原样，不做颜色处理（之前转白是为了
        // 在深色 ImGui 背景上可见；原生图标保持原生原样。
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
        _tabCapLeft?.Dispose();   _tabCapLeft = null;
        _tabMiddle?.Dispose();    _tabMiddle = null;
        _tabCapRight?.Dispose();  _tabCapRight = null;
        _tabDivider?.Dispose();   _tabDivider = null;
        _tabIndicator?.Dispose(); _tabIndicator = null;
        _resizeHandleNormal?.Dispose();      _resizeHandleNormal = null;
        _resizeHandleHighlighted?.Dispose(); _resizeHandleHighlighted = null;
    }
}