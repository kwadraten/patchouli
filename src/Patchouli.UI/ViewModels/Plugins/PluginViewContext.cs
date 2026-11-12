using System.Text;
using System.Text.Json;
using Patchouli.UI.Themes;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// The host-side context snapshot pushed into every plugin web view. It carries the current
/// theme mode and effective scheme, every active palette variable (semantic key → hex), and the
/// canonical URI of the page currently shown in the PDF workspace when the view lives in its
/// sidebar (null for tab and window placements). This is the entirety of what a plugin web view
/// can learn about the host: no tokens, objects or capabilities.
/// </summary>
public sealed record PluginViewContext(
    string ThemeMode,
    string Scheme,
    bool IsDark,
    IReadOnlyDictionary<string, string> Colors,
    string? PageUri);

/// <summary>Builds <see cref="PluginViewContext"/> snapshots from the theme/palette statics.</summary>
public static class PluginViewContextBuilder
{
    /// <summary>CSS custom-property prefix mirroring every palette variable onto :root.</summary>
    public const string CssVariablePrefix = "--patchouli-color-";

    public static PluginViewContext Build(string? pageUri)
    {
        bool isDark = ThemePaletteApplier.CurrentEffectiveIsDark;
        IReadOnlyDictionary<string, string> colors = UiColorPalettes
            .Resolve(ThemePaletteApplier.CurrentPaletteId)
            .GetColors(isDark);

        Dictionary<string, string> variables = new(colors.Count, StringComparer.Ordinal);
        foreach ((string key, string hex) in colors)
        {
            variables[key] = hex;
        }

        return new PluginViewContext(
            ThemePaletteApplier.CurrentThemeMode,
            isDark ? "dark" : "light",
            isDark,
            variables,
            pageUri);
    }

    /// <summary>The --patchouli-color-* custom property for one palette key: the trailing
    /// "Color" suffix is dropped and the rest kebab-cased — SurfaceColor →
    /// --patchouli-color-surface, OnSurfaceVariantColor → --patchouli-color-on-surface-variant.</summary>
    internal static string CssVariableName(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        string stem = key.EndsWith("Color", StringComparison.Ordinal) ? key[..^5] : key;
        return CssVariablePrefix + ToKebabCase(stem);
    }

    /// <summary>PascalCase → kebab-case, splitting acronym runs on the last capital before a
    /// lower-case letter: OnURL → on-url. Deterministic and lossless.</summary>
    internal static string ToKebabCase(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        StringBuilder builder = new(key.Length + 8);
        for (int i = 0; i < key.Length; i++)
        {
            char ch = key[i];
            if (char.IsUpper(ch) && i > 0 &&
                (char.IsLower(key[i - 1]) || char.IsDigit(key[i - 1]) ||
                 (i + 1 < key.Length && char.IsLower(key[i + 1]))))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}

/// <summary>
/// JavaScript bridges between the host and plugin web views. The context is delivered two ways:
/// an init script injected into the document head before the view's own markup runs (so
/// <c>window.patchouli</c> is available synchronously at parse time), and a push script
/// evaluated through <c>InvokeScript</c> that replaces the object, refreshes the CSS custom
/// properties and dispatches <c>patchouli:theme</c>/<c>patchouli:page</c> CustomEvents. The
/// supported web→host messages are
/// <c>invokeCSharpAction({"type":"patchouli:request-context"})</c>, answered with a push, and
/// <c>{"type":"patchouli:plugin-request",...}</c>, the permission-gated own-plugin
/// fetch/send/files bridge answered with a <c>patchouli:plugin-response</c> CustomEvent.
/// </summary>
public static class PluginViewContextScript
{
    public const string RequestContextMessageType = "patchouli:request-context";
    public const string ThemeEventName = "patchouli:theme";
    public const string PageEventName = "patchouli:page";

    /// <summary>
    /// View→host message type for permission-gated own-plugin fetch/send calls; replies are
    /// pushed back as <see cref="PluginResponseEventName"/> CustomEvents on window.
    /// </summary>
    public const string PluginRequestMessageType = "patchouli:plugin-request";

    /// <summary>DOM event carrying the serialized bridge reply for a plugin request.</summary>
    public const string PluginResponseEventName = "patchouli:plugin-response";

    /// <summary>
    /// Content-Security-Policy injected into every loaded view document: no network fetches,
    /// frames, workers, forms or objects; only inline script/style plus data: images/fonts.
    /// The web view is a document surface, not a page on the open web.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
        "img-src data:; font-src data:; media-src data:; connect-src 'none'; " +
        "frame-src 'none'; object-src 'none'; worker-src 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>Base URI handed to NavigateToString; the document stays origin-less.</summary>
    public static readonly Uri DocumentBaseUri = new("about:blank");

    /// <summary>JSON for one context snapshot (System.Text.Json escapes for safe embedding).</summary>
    public static string Serialize(PluginViewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return JsonSerializer.Serialize(new
        {
            version = 1,
            theme = new
            {
                mode = context.ThemeMode,
                scheme = context.Scheme,
                dark = context.IsDark,
                colors = context.Colors
            },
            page = context.PageUri is null ? null : new { uri = context.PageUri }
        });
    }

    /// <summary>Inline script injected into the document head: exposes the initial context,
    /// applies the palette CSS variables and installs the patchouli.fetch/patchouli.send
    /// promise APIs before the view's own markup runs.</summary>
    public static string BuildInitScript(PluginViewContext context)
    {
        return BuildApplyScript(context, false) + BuildApiScript();
    }

    /// <summary>
    /// Installs <c>window.patchouli.fetch(path)</c>,
    /// <c>window.patchouli.send(path, action, args, requestId)</c>,
    /// <c>window.patchouli.files()</c> and <c>window.patchouli.ensureFiles()</c> once. Each
    /// call sends a <c>patchouli:plugin-request</c> message through invokeCSharpAction and
    /// resolves with the reply value delivered by the <see cref="PluginResponseEventName"/>
    /// event (or rejects with its error). Pending calls are capped at 16 and time out after
    /// 60s — except <c>ensureFiles</c>, which waits up to 15 minutes because a confirmed
    /// managed-file download can take that long for multi-GB models; the host-side operation
    /// itself stays cancellation-aware through the modal runner, so the client timeout is
    /// purely a reply-wait bound. <c>files()</c>/<c>ensureFiles()</c> resolve to
    /// <c>{state, files:[{path, state, expected_bytes?, sha256?, version?}]}</c> —
    /// local paths and download URLs are never exposed. Function identity is stable:
    /// context pushes only replace version/theme/page fields.
    /// </summary>
    public static string BuildApiScript()
    {
        return "(function(){" +
               "var w=window.patchouli=window.patchouli||{};" +
               "if(w.fetch&&w.send&&w.files&&w.ensureFiles){return;}" +
               "var pending={};var nextId=1;var MAX_PENDING=16;var TIMEOUT_MS=60000;" +
               "var ENSURE_TIMEOUT_MS=900000;" +
               "function pendingCount(){var n=0;for(var k in pending){n++;}return n;}" +
               "window.addEventListener('" + PluginResponseEventName + "',function(ev){" +
               "var d=ev.detail;" +
               "if(!d||typeof d.id!=='string'){return;}" +
               "var p=pending[d.id];" +
               "if(!p){return;}" +
               "delete pending[d.id];" +
               "clearTimeout(p.timer);" +
               "if(d.ok){p.resolve(d.value);}else{" +
               "var e=new Error(d.error&&d.error.message||'plugin request failed');" +
               "e.code=d.error&&d.error.code;" +
               "p.reject(e);}});" +
               "function request(operation,path,action,args,requestId,timeoutMs){" +
               "return new Promise(function(resolve,reject){" +
               "if(pendingCount()>=MAX_PENDING){" +
               "reject(new Error('too many pending plugin requests'));return;}" +
               "var id='preq'+(nextId++);" +
               "var timer=setTimeout(function(){" +
               "if(pending[id]){delete pending[id];reject(new Error('plugin request timed out'));}}," +
               "timeoutMs||TIMEOUT_MS);" +
               "pending[id]={resolve:resolve,reject:reject,timer:timer};" +
               "var message={type:'" + PluginRequestMessageType + "',id:id,operation:operation};" +
               "if(path!==undefined&&path!==null){message.path=path;}" +
               "if(action!==undefined&&action!==null){message.action=action;}" +
               "if(args!==undefined){message.args=args;}" +
               "if(requestId!==undefined&&requestId!==null){message.request_id=requestId;}" +
               "try{invokeCSharpAction(JSON.stringify(message));}catch(e){" +
               "clearTimeout(timer);delete pending[id];reject(e);}});}" +
               "w.fetch=function(path){return request('fetch',path);};" +
               "w.send=function(path,action,args,requestId){" +
               "return request('send',path,action,args,requestId);};" +
               "w.files=function(){return request('files',null,'status');};" +
               "w.ensureFiles=function(){" +
               "return request('files',null,'ensure',undefined,undefined,ENSURE_TIMEOUT_MS);};" +
               "})();";
    }

    /// <summary>Script evaluated via InvokeScript to deliver one bridge reply JSON to the
    /// live document: dispatches a <see cref="PluginResponseEventName"/> CustomEvent whose
    /// detail is the reply object ({id, ok, value|error}).</summary>
    public static string BuildResponseDispatchScript(string replyJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(replyJson);
        return "window.dispatchEvent(new CustomEvent('" + PluginResponseEventName +
               "',{detail:" + replyJson + "}));";
    }

    /// <summary>Script evaluated via InvokeScript: replaces the context object, refreshes the
    /// CSS variables and notifies listeners through patchouli:theme and patchouli:page
    /// CustomEvents (the page event always fires; its detail is null without a page context).</summary>
    public static string BuildPushScript(PluginViewContext context)
    {
        return BuildApplyScript(context, true);
    }

    /// <summary>True only for the single supported web→host message; anything malformed,
    /// missing or of another type is ignored by the host.</summary>
    public static bool IsContextRequestMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("type", out JsonElement type) &&
                   type.ValueKind == JsonValueKind.String &&
                   string.Equals(type.GetString(), RequestContextMessageType, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Injects the CSP meta tag and the initial-context script at the top of the document head
    /// so they precede any view markup. Falls back to synthesizing a head (or prepending) for
    /// documents that do not declare one — the injection is never skipped.
    /// </summary>
    public static string PrepareHtml(string html, PluginViewContext context)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(context);

        string injection = "<meta http-equiv=\"Content-Security-Policy\" content=\"" +
                           ContentSecurityPolicy + "\"><script>" + BuildInitScript(context) + "</script>";

        int head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (head >= 0)
        {
            int headEnd = html.IndexOf('>', head);
            if (headEnd >= 0)
            {
                return html.Insert(headEnd + 1, injection);
            }
        }

        int htmlTag = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (htmlTag >= 0)
        {
            int tagEnd = html.IndexOf('>', htmlTag);
            if (tagEnd >= 0)
            {
                return html.Insert(tagEnd + 1, "<head>" + injection + "</head>");
            }
        }

        return injection + html;
    }

    /// <summary>
    /// One shared application routine: assign window.patchouli, mirror every palette variable
    /// onto :root as --patchouli-color-* plus color-scheme/data attribute, and optionally raise
    /// the DOM events (push only — on init no listener can exist yet).
    /// </summary>
    private static string BuildApplyScript(PluginViewContext context, bool dispatchEvents)
    {
        StringBuilder variables = new(context.Colors.Count * 48);
        variables.Append('{');
        bool first = true;
        foreach ((string key, string hex) in context.Colors)
        {
            if (!first)
            {
                variables.Append(',');
            }

            first = false;
            variables.Append(JsonSerializer.Serialize(PluginViewContextBuilder.CssVariableName(key)));
            variables.Append(':');
            variables.Append(JsonSerializer.Serialize(hex));
        }

        variables.Append('}');

        StringBuilder script = new(512 + variables.Length);
        script.Append("(function(){var p=").Append(Serialize(context))
            .Append(";var w=window.patchouli=window.patchouli||{};")
            .Append("w.version=p.version;w.theme=p.theme;w.page=p.page;var r=document.documentElement;if(r){")
            .Append("r.style.colorScheme=p.theme.scheme;")
            .Append("r.setAttribute('data-patchouli-color-scheme',p.theme.scheme);")
            .Append("var v=").Append(variables)
            .Append(";for(var k in v){r.style.setProperty(k,v[k]);}}");
        if (dispatchEvents)
        {
            script.Append("window.dispatchEvent(new CustomEvent('").Append(ThemeEventName)
                .Append("',{detail:p.theme}));")
                .Append("window.dispatchEvent(new CustomEvent('").Append(PageEventName)
                .Append("',{detail:p.page}));");
        }

        return script.Append("})();").ToString();
    }
}
