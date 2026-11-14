using System;
using System.Collections.Generic;
using System.Linq;

namespace Patchouli.UI.ViewModels.Settings;

public sealed record SettingsSearchEntry(
    string SectionId,
    string CardAnchorId,
    string Category,
    string Page,
    string Title,
    IReadOnlyList<string> Keywords)
{
    public string DisplayPath => $"{Category} › {Page} › {Title}";

    public string SearchText => string.Join(' ', new[] { Title, DisplayPath }.Concat(Keywords));
}

/// <summary>Static settings index used by the toolbar search in <see cref="SearchMode.Settings"/>.
/// Every entry names a real section id that <see cref="SettingsViewModel.SelectSectionAsync"/>
/// accepts and a card anchor rendered by a <c>SettingsCard</c>. Extension configuration is indexed
/// by name only and navigates to the catalog card that owns the list.</summary>
public static class SettingsSearchIndex
{
    public static IReadOnlyList<SettingsSearchEntry> Entries { get; } =
    [
        new("appearance", "appearance_theme", "显示", "主题与阅读", "主题与配色",
            ["主题", "外观", "明暗", "配色", "颜色", "theme", "palette"]),
        new("appearance", "appearance_reading", "显示", "主题与阅读", "阅读",
            ["阅读", "字体", "字号", "对照翻译"]),

        new("startup", "startup_database", "行为", "启动", "启动",
            ["启动", "记住", "上次", "数据库", "database", "remember"]),
        new("system_integration", "system_protocol", "行为", "链接与系统", "patchouli:// 协议",
            ["协议", "注册", "patchouli", "链接", "uri", "protocol"]),
        new("system_integration", "system_activation", "行为", "链接与系统", "前台显示",
            ["前台", "激活", "窗口", "最小化", "托盘", "activate", "window"]),
        new("system_integration", "system_cli", "行为", "链接与系统", "命令行工具",
            ["命令行", "cli", "path", "patchouli-cli"]),
        new("system_integration", "system_proxy", "行为", "链接与系统", "网络代理",
            ["代理", "proxy", "端口", "排除", "bypass", "socks5"]),
        new("update", "update_check", "行为", "更新", "自动检查更新",
            ["更新", "检查", "版本", "update"]),

        new("library", "library_roots", "书库", "文件与扫描", "文件搜索目录",
            ["文件", "搜索目录", "扫描", "源文件", "路径"]),
        new("library", "library_exclusions", "书库", "文件与扫描", "排除规则",
            ["排除", "规则", "正则", "exclude"]),
        new("library", "library_paths", "书库", "文件与扫描", "路径信息",
            ["路径", "数据库", "同步根"]),

        new("import", "import_threshold", "数据", "导入", "整本失败阈值",
            ["导入", "失败页", "阈值", "import"]),
        new("import", "import_behavior", "数据", "导入", "文件处理行为",
            ["导入", "文件", "拖放", "mcp add", "移动"]),
        new("local_files", "local_renders", "数据", "本机存储", "页面渲染缓存",
            ["缩略图", "缓存", "渲染", "render", "清理", "png"]),
        new("local_files", "local_managed", "数据", "本机存储", "托管位置",
            ["模型", "扩展", "插件", "文件", "下载", "存储"]),

        new("ocr_presets", "ocr_presets", "文献加工", "OCR 预设", "OCR 预设",
            ["ocr", "预设", "并行", "引擎"]),
        new("ocr_components", "ocr_components_layout", "文献加工", "OCR 组件", "OCR 组件",
            ["ocr", "版面", "识别", "引擎", "文本层", "组件"]),
        new("translation", "translation_defaults", "文献加工", "翻译", "文献翻译",
            ["翻译", "语言", "引擎", "translation"]),
        new("metadata", "metadata_sources", "文献加工", "元数据来源", "来源优先级",
            ["元数据", "来源", "优先级", "metadata"]),

        new("llm", "llm_default", "AI 集成", "模型连接", "默认模型",
            ["模型", "服务商", "api key", "base url", "llm", "provider"]),
        new("agents", "agents_integration", "AI 集成", "Agent", "Agent 集成",
            ["agent", "集成", "路径", "skill", "终端"]),
        new("mcp", "mcp_service", "AI 集成", "MCP 服务", "服务状态",
            ["mcp", "端口", "token", "cors", "外部", "访问", "监听"]),
        new("permissions", "permissions_matrix", "AI 集成", "访问权限", "资源访问权限",
            ["权限", "访问", "资源", "vfs", "文库", "可见", "permission"]),

        new("search_rewrite", "search_rewrite", "搜索", "搜索重写", "重写规则",
            ["搜索", "重写", "规则", "opencc"]),
        new("search_awareness", "search_awareness", "搜索", "页面感知", "页面感知",
            ["页面", "感知", "默认", "模式", "搜索框"]),
        new("search_links", "search_links", "搜索", "链接解析", "链接解析",
            ["链接", "虚拟", "patchouli", "搜索栏", "解析"]),

        new("csl_localization", "csl_localization", "引用", "本地化", "默认本地化",
            ["题录", "本地化", "语言", "csl"]),
        new("csl_installed", "csl_installed", "引用", "已安装样式", "已安装样式",
            ["csl", "样式", "默认", "引用"]),
        new("csl_online", "csl_online", "引用", "在线样式", "在线样式",
            ["在线", "样式", "下载", "搜索", "csl"]),
        new("csl_cayw", "csl_cayw", "引用", "Cite As You Write", "服务状态",
            ["csl", "cayw", "引用", "服务", "端口", "zotero"]),

        new("sync", "sync_binding", "同步", "同步", "本机同步绑定",
            ["同步", "设备", "根目录", "sync"]),
        new("sync", "sync_scope", "同步", "同步", "同步范围",
            ["同步", "范围", "设置项", "快照"])
    ];

    public static IReadOnlyList<SettingsSearchEntry> Search(
        SettingsViewModel settings,
        string query,
        int limit = 8)
    {
        string normalized = Normalize(query);
        if (normalized.Length == 0)
        {
            return [];
        }

        return Entries
            .Concat(DynamicEntries(settings))
            .Select(entry => (Entry: entry, Score: Score(entry, normalized)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Entry.DisplayPath, StringComparer.CurrentCulture)
            .Take(limit)
            .Select(candidate => candidate.Entry)
            .ToArray();
    }

    private static IEnumerable<SettingsSearchEntry> DynamicEntries(SettingsViewModel settings)
    {
        foreach (OcrComponentRowViewModel component in settings.LayoutModelCatalog.Components)
        {
            yield return ExtensionEntry("ocr_components", "ocr_components_layout", "文献加工", "OCR 组件",
                component.Name, component.Summary);
        }

        foreach (OcrComponentRowViewModel component in settings.OcrEngineCatalog.Components)
        {
            yield return ExtensionEntry("ocr_components", "ocr_components_engines", "文献加工", "OCR 组件",
                component.Name, component.Summary);
        }

        foreach (OcrComponentRowViewModel component in settings.OcrTextLayerCatalog.Components)
        {
            yield return ExtensionEntry("ocr_components", "ocr_components_textlayer", "文献加工", "OCR 组件",
                component.Name, component.Summary);
        }

        foreach (ExtensionListItem extension in settings.LlmSettings.TranslationEngineOptions)
        {
            yield return ExtensionEntry("translation", "translation_defaults", "文献加工", "翻译",
                extension.Name, extension.Summary);
        }

        foreach (MetadataSourceSettingsRowViewModel source in settings.MetadataLookupSettings.Sources)
        {
            yield return ExtensionEntry("metadata", "metadata_sources", "文献加工", "元数据来源",
                source.Name, source.Description);
        }
    }

    private static SettingsSearchEntry ExtensionEntry(
        string sectionId,
        string anchorId,
        string category,
        string page,
        string name,
        string description)
    {
        string[] keywords = description
            .Split([' ', '\r', '\n', '，', '。', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Take(6)
            .ToArray();
        return new SettingsSearchEntry(sectionId, anchorId, category, page, name, keywords);
    }

    private static int Score(SettingsSearchEntry entry, string normalizedQuery)
    {
        string title = Normalize(entry.Title);
        if (title.Equals(normalizedQuery, StringComparison.Ordinal))
        {
            return 100;
        }

        if (title.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            return 80;
        }

        if (title.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return 60;
        }

        int best = 0;
        foreach (string keyword in entry.Keywords)
        {
            string normalizedKeyword = Normalize(keyword);
            if (normalizedKeyword.Equals(normalizedQuery, StringComparison.Ordinal))
            {
                best = Math.Max(best, 50);
            }
            else if (normalizedKeyword.Contains(normalizedQuery, StringComparison.Ordinal))
            {
                best = Math.Max(best, 30);
            }
        }

        if (best == 0 && Normalize(entry.DisplayPath).Contains(normalizedQuery, StringComparison.Ordinal))
        {
            best = 20;
        }

        return best;
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant();
    }
}
