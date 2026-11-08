using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.Themes;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Controls;

/// <summary>
/// Lazily-hosted <see cref="NativeWebView"/> for plugin-declared HTML views. The native web view
/// is only created while the host is attached and effectively visible with a session bound, and
/// it is released again when the host hides, detaches or <see cref="Release"/> is called — so a
/// closed tab/window/sidebar never keeps a live web view, and headless tests never construct
/// one (they exercise the view-model layer instead).
///
/// Security surface: only about: and data: navigations happen in place (the document is loaded
/// via NavigateToString); http(s) targets open in the system browser; every other scheme is
/// blocked. Dev tools are disabled, a strict CSP meta tag is injected into the document head,
/// and the only host capability exposed to content is the patchouli:request-context message
/// answering with a theme/palette/page snapshot — no tokens or objects.
/// </summary>
public sealed class PluginWebViewHost : ContentControl
{
    public static readonly StyledProperty<PluginViewSession?> SessionProperty =
        AvaloniaProperty.Register<PluginWebViewHost, PluginViewSession?>(nameof(Session));

    private readonly List<Visual> _observedAncestors = [];
    private NativeWebView? _webView;
    private PluginViewSession? _session;
    private string? _loadedHtml;
    private bool _themeSubscribed;

    public PluginViewSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    /// <summary>Test seam: the native web view factory. Headless tests never create one.</summary>
    internal Func<NativeWebView>? WebViewFactory { get; set; }

    /// <summary>Test seam: external-URI launch. Defaults to the top-level platform launcher.</summary>
    internal Func<Uri, Task>? ExternalLauncher { get; set; }

    /// <summary>Test-visible state: whether the native web view is currently materialized.</summary>
    internal bool HasLiveWebView => _webView is not null;

    /// <summary>Releases the native web view and every subscription. Idempotent; the host can be
    /// re-enabled later by binding a session while visible.</summary>
    public void Release()
    {
        ReleaseWebView();
        UnsubscribeSession();
        UnsubscribeTheme();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionProperty)
        {
            UnsubscribeSession();
            _session = change.GetNewValue<PluginViewSession?>();
            SubscribeSession();
            _loadedHtml = null;
            if (_webView is not null && _session?.Html is not null)
            {
                Navigate(_session.Html);
            }
        }
        else if (change.Property == IsVisibleProperty)
        {
            UpdateWebViewVisibility();
        }
    }

    /// <summary>
    /// Effective visibility is what matters (a retained tab page or an inactive sidebar surface
    /// is hidden via an ancestor's IsVisible, not detached), but Avalonia exposes no public
    /// notification for it — so while attached we watch every visual ancestor's IsVisible.
    /// </summary>
    private void ObserveAncestors()
    {
        UnobserveAncestors();
        foreach (Visual ancestor in this.GetVisualAncestors())
        {
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
            _observedAncestors.Add(ancestor);
        }
    }

    private void UnobserveAncestors()
    {
        foreach (Visual ancestor in _observedAncestors)
        {
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        }

        _observedAncestors.Clear();
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            UpdateWebViewVisibility();
        }
    }

    private void UpdateWebViewVisibility()
    {
        if (IsEffectivelyVisible)
        {
            EnsureWebView();
        }
        else
        {
            // Never keep a live web view running under an invisible container.
            ReleaseWebView();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeTheme();
        ObserveAncestors();
        EnsureWebView();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseWebView();
        UnsubscribeTheme();
        UnobserveAncestors();
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeSession()
    {
        if (_session is not null)
        {
            _session.PropertyChanged += OnSessionPropertyChanged;
        }
    }

    private void UnsubscribeSession()
    {
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
            _session = null;
        }
    }

    private void SubscribeTheme()
    {
        if (!_themeSubscribed)
        {
            ThemePaletteApplier.Applied += OnThemeApplied;
            _themeSubscribed = true;
        }
    }

    private void UnsubscribeTheme()
    {
        if (_themeSubscribed)
        {
            ThemePaletteApplier.Applied -= OnThemeApplied;
            _themeSubscribed = false;
        }
    }

    private void OnThemeApplied()
    {
        PushContext();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PluginViewSession.Html):
                if (!string.Equals(_loadedHtml, _session?.Html, StringComparison.Ordinal))
                {
                    Navigate(_session?.Html);
                }

                break;
            case nameof(PluginViewSession.PageUri):
                PushContext();
                break;
        }
    }

    private void EnsureWebView()
    {
        if (_webView is not null || _session is null || !IsEffectivelyVisible || VisualRoot is null)
        {
            return;
        }

        NativeWebView webView = WebViewFactory?.Invoke() ?? new NativeWebView();
        _webView = webView;
        webView.EnvironmentRequested += OnEnvironmentRequested;
        webView.NavigationStarted += OnNavigationStarted;
        webView.NavigationCompleted += OnNavigationCompleted;
        webView.NewWindowRequested += OnNewWindowRequested;
        webView.WebMessageReceived += OnWebMessageReceived;
        Content = webView;
        Navigate(_session.Html);
    }

    private void ReleaseWebView()
    {
        if (_webView is not { } webView)
        {
            return;
        }

        _webView = null;
        _loadedHtml = null;
        webView.EnvironmentRequested -= OnEnvironmentRequested;
        webView.NavigationStarted -= OnNavigationStarted;
        webView.NavigationCompleted -= OnNavigationCompleted;
        webView.NewWindowRequested -= OnNewWindowRequested;
        webView.WebMessageReceived -= OnWebMessageReceived;
        webView.Stop();
        Content = null;
    }

    private void Navigate(string? html)
    {
        if (_webView is null)
        {
            return;
        }

        _loadedHtml = html;
        if (html is null)
        {
            _webView.Navigate(PluginViewContextScript.DocumentBaseUri);
            return;
        }

        PluginViewContext context = PluginViewContextBuilder.Build(_session?.PageUri);
        _webView.NavigateToString(
            PluginViewContextScript.PrepareHtml(html, context),
            PluginViewContextScript.DocumentBaseUri);
    }

    private void PushContext()
    {
        if (_webView is null)
        {
            return;
        }

        PluginViewContext context = PluginViewContextBuilder.Build(_session?.PageUri);
        _webView.InvokeScript(PluginViewContextScript.BuildPushScript(context))
            .Observe(nameof(PluginWebViewHost), "push-context");
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        Uri? request = e.Request;
        if (request is null)
        {
            e.Cancel = true;
            return;
        }

        // In-place navigation is limited to the loaded document itself.
        if (request.Scheme is "about" or "data")
        {
            return;
        }

        e.Cancel = true;
        if (request.Scheme is "http" or "https")
        {
            OpenExternal(request);
        }
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is { Scheme: "http" or "https" } target)
        {
            OpenExternal(target);
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            PushContext();
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        // The single supported web→host call; malformed or other types are ignored.
        if (PluginViewContextScript.IsContextRequestMessage(e.Body))
        {
            PushContext();
        }
    }

    private void OpenExternal(Uri uri)
    {
        try
        {
            if (ExternalLauncher is { } launcher)
            {
                launcher(uri).Observe(nameof(PluginWebViewHost), "external-launch");
                return;
            }

            ILauncher? platformLauncher = TopLevel.GetTopLevel(this)?.Launcher;
            if (platformLauncher is not null)
            {
                platformLauncher.LaunchUriAsync(uri).Observe(nameof(PluginWebViewHost), "external-launch");
            }
            else
            {
                using Process? browser =
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, nameof(PluginWebViewHost), "external-launch");
        }
    }
}
