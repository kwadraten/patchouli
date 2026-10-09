using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.Services;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.AgentChat;

namespace Patchouli.UI.Views;

/// <summary>
///     The chat tab's view. It owns nothing about the session: showing the tab starts the view
///     model's periodic refresh and hiding it stops that refresh, which is exactly the "closing
///     removes the front end only" contract (D9) — the run keeps going.
/// </summary>
public sealed partial class AgentChatPage : UserControl, IWorkspaceTabPage
{
    private AgentChatTabViewModel? _chat;
    private bool _followLatest = true;
    private bool? _isNarrow;
    private bool _isActive;
    private bool _isClosed;
    private bool _pendingLatest;
    private int _lifecycleGeneration;

    /// <summary>Creates the view.</summary>
    public AgentChatPage()
    {
        InitializeComponent();
        MessageBox.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel, true);
        SizeChanged += OnPageSizeChanged;
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach(DataContext as AgentChatTabViewModel);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as AgentChatTabViewModel);
        OnTabActivated();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        OnTabDeactivated();
        Attach(null);
    }

    private void Attach(AgentChatTabViewModel? chat)
    {
        if (ReferenceEquals(_chat, chat))
        {
            return;
        }

        if (_chat is not null)
        {
            if (_isActive)
            {
                _chat.Deactivate();
            }

            _chat.Messages.CollectionChanged -= OnMessagesChanged;
        }

        _chat = chat;
        if (chat is not null && !_isClosed)
        {
            chat.Messages.CollectionChanged += OnMessagesChanged;
            if (_isActive && !_isClosed)
            {
                _followLatest = true;
                _pendingLatest = false;
                int generation = ++_lifecycleGeneration;
                _ = ActivateAsync(chat, generation);
            }
        }
    }

    public void OnTabActivated()
    {
        if (_isClosed || _isActive)
        {
            return;
        }

        _isActive = true;
        int generation = ++_lifecycleGeneration;
        if (_pendingLatest && _followLatest)
        {
            _pendingLatest = false;
            PostScrollToLatest(generation);
        }

        if (_chat is { } chat)
        {
            _ = ActivateAsync(chat, generation);
        }
    }

    public void OnTabDeactivated()
    {
        if (!_isActive)
        {
            return;
        }

        _isActive = false;
        ++_lifecycleGeneration;
        _chat?.Deactivate();
    }

    public void OnTabClosed()
    {
        if (_isClosed)
        {
            return;
        }

        OnTabDeactivated();
        _isClosed = true;
        ++_lifecycleGeneration;
        Attach(null);
        SizeChanged -= OnPageSizeChanged;
        MessageBox.RemoveHandler(KeyDownEvent, OnComposerKeyDown);
    }

    private async Task ActivateAsync(AgentChatTabViewModel chat, int generation)
    {
        try
        {
            await chat.ActivateAsync();
            if (_isActive && !_isClosed && generation == _lifecycleGeneration && ReferenceEquals(_chat, chat) &&
                _pendingLatest && _followLatest)
            {
                _pendingLatest = false;
                PostScrollToLatest(generation);
            }
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "agent-chat", "activate");
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _followLatest = true;
        }

        if (_followLatest)
        {
            if (_isActive)
            {
                PostScrollToLatest(_lifecycleGeneration);
            }
            else
            {
                _pendingLatest = true;
            }
        }
    }

    private void PostScrollToLatest(int generation)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isActive && !_isClosed && generation == _lifecycleGeneration && _followLatest)
            {
                ScrollToLatest();
            }
        }, DispatcherPriority.Loaded);
    }

    private void ScrollToLatest()
    {
        MessageScroll.ScrollToEnd();
    }

    private void OnMessageScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_isActive || _isClosed)
        {
            return;
        }

        if (e.ExtentDelta.Y != 0 && _followLatest)
        {
            PostScrollToLatest(_lifecycleGeneration);
        }
        else if (e.ExtentDelta.Y == 0)
        {
            _followLatest = MessageScroll.Offset.Y >=
                            MessageScroll.Extent.Height - MessageScroll.Viewport.Height - 48;
        }
    }

    private void OnPageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 820;
        if (e.NewSize.Width > 0 && narrow != _isNarrow && _chat is { } chat)
        {
            _isNarrow = narrow;
            chat.IsSidebarOpen = !narrow;
        }
    }

    private void OnSessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_chat is { IsSending: false } chat &&
            e.AddedItems.OfType<AgentChatSessionViewModel>().FirstOrDefault() is { } session &&
            !ReferenceEquals(chat.SelectedSession, session))
        {
            _ = SelectSessionAsync(chat, session, _lifecycleGeneration);
        }
    }

    private async Task SelectSessionAsync(
        AgentChatTabViewModel chat, AgentChatSessionViewModel session, int generation)
    {
        try
        {
            await chat.SelectSessionAsync(session);
            if (!_isActive || _isClosed || generation != _lifecycleGeneration ||
                !ReferenceEquals(_chat, chat) || !ReferenceEquals(chat.SelectedSession, session))
            {
                return;
            }

            _followLatest = true;
            PostScrollToLatest(_lifecycleGeneration);
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "agent-chat", "select-session");
        }
    }

    private void OnSuggestionClick(object? sender, RoutedEventArgs e)
    {
        if (_chat is { IsNewSession: true, IsSending: false } chat && sender is Button { Tag: string prompt })
        {
            chat.MessageInput = prompt;
            MessageBox.Focus();
            MessageBox.CaretIndex = prompt.Length;
        }
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && _chat is { } chat)
        {
            e.Handled = true;
            if (chat.SendMessageCommand.CanExecute(null))
            {
                chat.SendMessageCommand.Execute(null);
            }
        }
    }

    private void OnCopyMessageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AgentChatMessageViewModel message })
        {
            _ = CopyMessageAsync(message.IsActivity ? message.DetailText : message.Text);
        }
    }

    private void OnMessageLinkClick(object? sender, LinkClickedEventArgs e)
    {
        e.Handled = true;
        _ = OpenMessageLinkAsync(e.HRef);
    }

    private async Task OpenMessageLinkAsync(Uri? uri)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }

            MarkdownLinkNavigator navigator = new(
                target => topLevel.DataContext is MainWindowViewModel main
                    ? main.NavigateToVfsUriAsync(target)
                    : Task.CompletedTask,
                target => topLevel.Launcher.LaunchUriAsync(target));
            await navigator.NavigateAsync(uri);
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "agent-chat", "open-link");
        }
    }

    private async Task CopyMessageAsync(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "agent-chat", "copy-message");
        }
    }
}
