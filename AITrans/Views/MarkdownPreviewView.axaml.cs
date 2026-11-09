using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using AITrans.Models;
using AITrans.Services;
using AITrans.ViewModels;

namespace AITrans.Views;

public partial class MarkdownPreviewView : UserControl
{
    // Tracking styles we inject so we can remove them before updating
    private readonly List<Style> _fontSizeStyles = [];
    private int _pendingScrollParagraph = -1;
    private MarkdownPreviewViewModel? _subscribedVm;
    private ScrollViewer? _previewScroll;
    private bool _scrollHooked;
    private int _scrollHookRetryCount;
    private const int MaxScrollHookRetries = 30;
    // Deferred scroll restore — negative means nothing pending
    private double _pendingScrollY = -1;
    private int _restoreRetryCount;
    private const int MaxRestoreRetries = 30;
    private bool _restoringScroll;

    // WYSIWYG host (Desktop only — see WysiwygEditorProvider)
    private Control? _wysiwygControl;
    private IDisposable? _wysiwygThemeSub;

    // In-document search — tracks the last match's start so repeated Enter/▼ advances instead
    // of restarting from the caret each time; reset whenever the query text itself changes.
    private string _lastSearchQuery = "";
    private int _lastSearchMatchStart = -1;

    public MarkdownPreviewView()
    {
        InitializeComponent();
        // Track caret position in the raw editor for TTS "Read from Selection"
        RawEditor.PointerReleased += OnRawEditorPointerReleased;
        RawEditor.KeyUp += OnRawEditorKeyUp;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;

        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm.DocumentLoaded -= OnVmDocumentLoaded;
        }
        _subscribedVm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.DocumentLoaded += OnVmDocumentLoaded;

        // Intercept hyperlink clicks — route .md links to in-app navigation, web links to the browser
        if (MarkViewer.Engine is global::Markdown.Avalonia.Markdown mdEngine)
        {
            mdEngine.HyperlinkCommand = new RelayCommand<string>(url =>
            {
                if (!string.IsNullOrEmpty(url))
                    vm.NavigateTo(url);
            });
        }

        // Apply the initial font size
        ApplyFontSizeStyles(vm.PreviewFontSize);
        PushWysiwygStyle(vm);
        HookScrollViewers();

        // DataContext comes from an inherited binding, so after a reattach it can arrive later
        // than OnAttachedToVisualTree — catch up here if WYSIWYG should be showing.
        if (VisualRoot is not null && IsVisible)
            ShowWysiwygIfNeeded(vm);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;

        if (change.GetNewValue<bool>())
        {
            HookScrollViewers(); // Ensure hooks are set up when tab becomes visible
            if (DataContext is MarkdownPreviewViewModel vm)
            {
                vm.RequestRestoreScroll();
                ShowWysiwygIfNeeded(vm);
            }
            Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
        }
        else
        {
            DiscardWysiwygControl();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.Root is Window win)
            win.Closing += OnWindowClosing;
        if (DataContext is MarkdownPreviewViewModel vm)
        {
            vm.RequestRestoreScroll();
            ShowWysiwygIfNeeded(vm);
        }
        Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
        HookScrollViewers();

        // ThemeTextBrush is defined by every theme (built-in Light/Dark and every custom theme
        // file alike — see Themes/*.axaml), so it doubles as a reliable "some theme-related
        // resource just changed" signal, whether that's a Light/Dark flip or swapping to a
        // different custom theme of the same variant (which doesn't raise ActualThemeVariantChanged).
        _wysiwygThemeSub = this.GetResourceObservable("ThemeTextBrush").Subscribe(new AnonymousObserver<object?>(_ =>
        {
            // Deferred: ThemeService flips RequestedThemeVariant before adding the custom theme's
            // styles, so resolve once both changes have landed.
            Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is MarkdownPreviewViewModel current) PushWysiwygStyle(current);
            }, DispatcherPriority.Background);
        }));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (e.Root is Window win)
            win.Closing -= OnWindowClosing;
        SavePreviewOffsetIfAvailable();
        if (DataContext is MarkdownPreviewViewModel vm)
            vm.PersistSessionState();
        UnhookScrollViewers();
        DiscardWysiwygControl();
        _wysiwygThemeSub?.Dispose();
        _wysiwygThemeSub = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        SavePreviewOffsetIfAvailable();
        if (DataContext is MarkdownPreviewViewModel vm)
            vm.PersistSessionState();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs pe)
    {
        if (sender is not MarkdownPreviewViewModel vm) return;

        // Update AssetPathRoot whenever the loaded file changes so relative images resolve correctly
        if (pe.PropertyName == nameof(MarkdownPreviewViewModel.LoadedFilePath)
            && !string.IsNullOrEmpty(vm.LoadedFilePath))
        {
            var dir = Path.GetDirectoryName(vm.LoadedFilePath);
            if (dir != null) MarkViewer.AssetPathRoot = dir;

            // Cancel any deferred scroll from the previous file and block
            // scroll-save events that fire while new content renders, to prevent
            // the old position from being saved under the new file's key.
            _pendingScrollY = -1;
            _restoringScroll = true;
            if (_previewScroll != null)
                _previewScroll.LayoutUpdated -= OnPreviewScrollLayoutUpdated;
        }

        // Rebuild font-size override styles when the user clicks A+/A- — same PreviewFontSize
        // drives both Rendered (Avalonia styles) and WYSIWYG (CSS), so they stay in lockstep.
        if (pe.PropertyName == nameof(MarkdownPreviewViewModel.PreviewFontSize))
        {
            ApplyFontSizeStyles(vm.PreviewFontSize);
            PushWysiwygStyle(vm);
        }

        // Scroll preview to top when page changes
        if (pe.PropertyName == nameof(MarkdownPreviewViewModel.CurrentPage))
            Dispatcher.UIThread.Post(ScrollPreviewToTop, DispatcherPriority.Loaded);

        if (pe.PropertyName == nameof(MarkdownPreviewViewModel.ScrollToParagraph) && vm.ScrollToParagraph >= 0)
        {
            _pendingScrollParagraph = vm.ScrollToParagraph;
            vm.ScrollToParagraph = -1; // consume the signal
            if (IsVisible)
                Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
        }

        // Scroll chat to bottom when a new message arrives
        if (pe.PropertyName == nameof(MarkdownPreviewViewModel.ChatScrollRequest))
            Dispatcher.UIThread.Post(ScrollChatToBottom, DispatcherPriority.Background);

        // TOC click — locate the heading on the (possibly just-switched) page and scroll to it
        if (pe.PropertyName == nameof(MarkdownPreviewViewModel.HeadingNavRequest) && vm.HeadingNavRequest is { } navRequest)
        {
            if (vm.IsWysiwygMode)
                FindInWysiwyg(navRequest.Title);
            else
                Dispatcher.UIThread.Post(() => ScrollToHeading(navRequest), DispatcherPriority.Loaded);
        }

        // "Отбелязани" click — the view-model already switched to the mark's page
        if (pe.PropertyName == nameof(MarkdownPreviewViewModel.MarkNavRequest) && vm.MarkNavRequest is { } markRequest)
            GoToMark(vm, markRequest.Mark);

        // Entering WYSIWYG mode or paging while already in it — (re)populate the editor with the
        // current page (a file load is handled by OnVmDocumentLoaded, see there). Edits made inside the editor flow back through
        // ApplyWysiwygPageEdit (see OnWysiwygMarkdownChanged) without touching CurrentPage, so
        // this branch doesn't re-fire and stomp on content the user is actively editing.
        // Scroll position is only restored on the first two (resuming where the user left off) —
        // a plain page turn starts at the top of the new page, same as Rendered mode's
        // ScrollPreviewToTop above.
        if (pe.PropertyName == nameof(MarkdownPreviewViewModel.DisplayMode))
        {
            if (vm.IsWysiwygMode)
                EnsureWysiwygContent(vm, restoreScroll: true);
            else
                DiscardWysiwygControl();
        }
        else if (pe.PropertyName == nameof(MarkdownPreviewViewModel.CurrentPage) && vm.IsWysiwygMode)
            EnsureWysiwygContent(vm, restoreScroll: false);

        // Search bar opened (via the 🔍 button or Ctrl+F) — focus it. IsSearchOpen only raises
        // when it actually flips to true, so a repeat Ctrl+F while already open is a no-op here;
        // acceptable since the bar is already visible and usable in that case.
        if (pe.PropertyName == nameof(MarkdownPreviewViewModel.IsSearchOpen) && vm.IsSearchOpen)
            Dispatcher.UIThread.Post(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Loaded);
    }

    // ── WYSIWYG editor (Desktop only) ───────────────────────────────────────

    /// <summary>A file finished loading. Not keyed off LoadedFilePath's change: the view-model
    /// sets the path BEFORE the content (so pagination resolves per-file settings), so pushing
    /// then sent the previous file's page — and for a one-page file CurrentPage stays 0, so
    /// nothing re-pushed afterwards and the editor kept stale or empty content.</summary>
    private void OnVmDocumentLoaded(object? sender, System.EventArgs e)
    {
        if (sender is MarkdownPreviewViewModel vm && vm.IsWysiwygMode && IsVisible && VisualRoot is not null)
            EnsureWysiwygContent(vm, restoreScroll: true);
    }

    /// <summary>Called whenever this view (re)appears — attached, made visible, or handed its
    /// DataContext. Creates a fresh WYSIWYG control if that mode is active and none exists.</summary>
    private void ShowWysiwygIfNeeded(MarkdownPreviewViewModel vm)
    {
        if (vm.IsWysiwygMode && _wysiwygControl is null)
            EnsureWysiwygContent(vm, restoreScroll: true);
    }

    /// <summary>Creates the WYSIWYG control if needed, then pushes the current page into it.
    /// A control is only ever shown once: every time WYSIWYG mode is left, or this view is hidden
    /// or detached (MainView's TabControl switching away), DiscardWysiwygControl drops it and the
    /// next show builds a brand-new one. Re-showing the same WebView instance — whether by
    /// toggling IsVisible or by detaching/reattaching it through WysiwygHost.Content — reliably
    /// leaves the native surface blank even though the page inside loads fine, while a first-ever
    /// show always works.</summary>
    private void EnsureWysiwygContent(MarkdownPreviewViewModel vm, bool restoreScroll = true)
    {
        if (_wysiwygControl is null)
        {
            try
            {
                _wysiwygControl = WysiwygEditorProvider.Factory?.Invoke();
            }
            catch (Exception ex)
            {
                vm.StatusText = $"WYSIWYG не е наличен: {ex.Message}";
                vm.DisplayMode = PreviewDisplayMode.Rendered;
                return;
            }

            if (_wysiwygControl is null)
            {
                vm.DisplayMode = PreviewDisplayMode.Rendered;
                return;
            }

            if (_wysiwygControl is IWysiwygEditorControl editor)
            {
                editor.MarkdownChanged += OnWysiwygMarkdownChanged;
                editor.ScrollChanged += OnWysiwygScrollChanged;
                editor.ErrorOccurred += OnWysiwygError;
                editor.ContextMenuRequested += OnWysiwygContextMenuRequested;
            }

            WysiwygHost.Content = _wysiwygControl;
            PushWysiwygStyle(vm);
        }

        PushWysiwygMarkdown(vm, restoreScroll);
    }

    private void PushWysiwygMarkdown(MarkdownPreviewViewModel vm, bool restoreScroll)
    {
        if (_wysiwygControl is not IWysiwygEditorControl ctrl) return;
        var scrollY = restoreScroll ? vm.GetWysiwygScrollY() : 0;
        ctrl.SetMarkdown(RewriteImagePaths(vm.GetWysiwygPageMarkdown(), vm.LoadedFilePath, toAbsolute: true), scrollY);
    }

    /// <summary>Unhooks and drops the current WYSIWYG control (see EnsureWysiwygContent for why it
    /// is never reused). Edits already reached the view-model through MarkdownChanged and the
    /// scroll position through ScrollChanged, so nothing is lost.</summary>
    private void DiscardWysiwygControl()
    {
        if (_wysiwygControl is null) return;
        if (_wysiwygControl is IWysiwygEditorControl editor)
        {
            editor.MarkdownChanged -= OnWysiwygMarkdownChanged;
            editor.ScrollChanged -= OnWysiwygScrollChanged;
            editor.ErrorOccurred -= OnWysiwygError;
            editor.ContextMenuRequested -= OnWysiwygContextMenuRequested;
        }
        WysiwygHost.Content = null;
        _wysiwygControl = null;
    }

    private void OnWysiwygScrollChanged(double y)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MarkdownPreviewViewModel vm) vm.SaveWysiwygScrollY(y);
        });
    }

    /// <summary>Right-click inside the WYSIWYG editor — shows the app's own menu at the click point
    /// (an Avalonia popup is its own native window, so it draws above the WebView). Works on the
    /// selection captured at right-click time, since the menu takes focus away from the page.</summary>
    private void OnWysiwygContextMenuRequested(string selectedText, double x, double y)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not MarkdownPreviewViewModel vm || _wysiwygControl is not { } target) return;
            var editor = target as IWysiwygEditorControl;
            var hasSelection = !string.IsNullOrWhiteSpace(selectedText);

            MenuItem Item(string header, Func<Task> action, bool enabled = true)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled };
                item.Click += async (_, _) => await action();
                return item;
            }

            var aiEnabled = hasSelection && !vm.IsChatBusy;
            var menu = new ContextMenu
            {
                PlacementTarget = target,
                Placement = PlacementMode.AnchorAndGravity,
                PlacementRect = new Rect(x, y, 1, 1),
                PlacementAnchor = PopupAnchor.TopLeft,
                PlacementGravity = PopupGravity.BottomRight,
                ItemsSource = new Control[]
                {
                    Item("📋 Копирай", () => CopyToClipboardAsync(vm, selectedText), hasSelection),
                    Item("✂ Изрежи", async () =>
                    {
                        await CopyToClipboardAsync(vm, selectedText);
                        editor?.InsertText("");
                    }, hasSelection),
                    Item("📥 Постави", async () =>
                    {
                        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                        if (clipboard is null) return;
#pragma warning disable CS0618
                        var text = await clipboard.GetTextAsync();
#pragma warning restore CS0618
                        if (!string.IsNullOrEmpty(text)) editor?.InsertText(text);
                    }),
                    new Separator(),
                    Item("🖍 Маркирай  (Ctrl+Shift+H)", () =>
                    {
                        if (CanMark(vm, selectedText)) editor?.InsertText($"=={selectedText}==");
                        return Task.CompletedTask;
                    }, hasSelection),
                    Item("📝 Маркирай с бележка…", async () =>
                    {
                        if (!CanMark(vm, selectedText)) return;
                        var note = await PromptForNoteAsync(selectedText);
                        if (note is null) return;
                        if (string.IsNullOrEmpty(note))
                            editor?.InsertText($"=={selectedText}==");
                        else
                            editor?.InsertMarkWithNote(selectedText, vm.NextFootnoteLabel(), note);
                    }, hasSelection),
                    new Separator(),
                    Item("🌐 Преведи", () => TranslateAsync(vm, selectedText), aiEnabled),
                    Item("💡 Обясни", () => ExplainAsync(vm, selectedText), aiEnabled),
                    Item("📝 Обобщи", () => SummarizeAsync(vm, selectedText), aiEnabled),
                    Item("📖 Добави дума", () => AddToWordListAsync(vm, selectedText), hasSelection),
                    new Separator(),
                    Item("🔊 Прочети оттук", () => vm.ReadFromTextAsync(hasSelection ? selectedText : null)),
                }
            };
            menu.Open(target);
        });
    }

    /// <summary>`==highlight==` is inline-only and doesn't nest, so a selection spanning
    /// paragraphs or already containing a highlight can't be wrapped.</summary>
    private static bool CanMark(MarkdownPreviewViewModel vm, string text)
    {
        if (text.Contains('\n') || text.Contains("=="))
        {
            vm.StatusText = "Маркирай текст в рамките на един абзац, без вече маркирани части.";
            return false;
        }
        return true;
    }

    /// <summary>Asks for the note text of a mark's footnote. Returns null on cancel, "" for
    /// "highlight without a note". A line break would end the one-line `[^n]: note` definition,
    /// so it's normalized away.</summary>
    private async Task<string?> PromptForNoteAsync(string markedText)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;

        var input = new TextBox { AcceptsReturn = false, Watermark = "Текст на бележката", MinWidth = 380 };
        string? result = null;
        var dialog = new Window
        {
            Title = "Бележка",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Отказ", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => { result = input.Text ?? ""; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"„{(markedText.Length > 80 ? markedText[..80] + "…" : markedText)}“",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 380,
                    FontStyle = FontStyle.Italic,
                },
                input,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel }
                }
            }
        };
        dialog.Opened += (_, _) => input.Focus();
        await dialog.ShowDialog(owner);

        return result?.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private async Task CopyToClipboardAsync(MarkdownPreviewViewModel vm, string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || string.IsNullOrEmpty(text)) return;
        await clipboard.SetTextAsync(text);
        vm.StatusText = "Текстът е копиран в клипборда.";
    }

    private void OnWysiwygError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MarkdownPreviewViewModel vm) vm.StatusText = $"WYSIWYG грешка: {message}";
        });
    }

    /// <summary>Pushes the app's current theme colors and Preview's font size into the WYSIWYG
    /// WebView (see IWysiwygEditorControl.ApplyHostStyle) — a no-op if the control doesn't exist
    /// yet, since EnsureWysiwygContent calls this again once it does.</summary>
    private void PushWysiwygStyle(MarkdownPreviewViewModel vm)
    {
        if (_wysiwygControl is not IWysiwygEditorControl editor) return;
        var (bg, text, codeBg, border) = ResolveWysiwygColors();
        editor.ApplyHostStyle(bg, text, codeBg, border, vm.PreviewFontSize);
    }

    /// <summary>Resolves the active theme's colors for the WYSIWYG WebView. Custom themes
    /// (Themes/*.axaml) define ThemeBackgroundBrush/ThemeCodeBackgroundBrush/ThemeControlBorderBrush;
    /// the built-in System/Light/Dark variants don't (see App.axaml's ThemeDictionaries, which
    /// only define ThemeTextBrush/ThemeAccentBrush), so those fall back to FluentTheme-equivalent
    /// colors picked from ActualThemeVariant.</summary>
    private (string Bg, string Text, string CodeBg, string Border) ResolveWysiwygColors()
    {
        var variant = ActualThemeVariant;
        var isDark = variant == ThemeVariant.Dark;

        // Must pass the variant explicitly: the overload without one resolves App.axaml's
        // ThemeDictionaries against the "Default" key, whose ThemeTextBrush is white — that's
        // what left WYSIWYG text white on light themes. This matches what {DynamicResource}
        // resolves for the Rendered preview.
        string Resolve(string key, string fallback) =>
            this.TryFindResource(key, variant, out var res) && res is ISolidColorBrush brush
                ? ToHex(brush.Color)
                : fallback;

        var bg = Resolve("ThemeBackgroundBrush", isDark ? "#1E1E1E" : "#FFFFFF");
        var text = Resolve("ThemeTextBrush", isDark ? "#F0F0F0" : "#1F1F1F");
        var codeBg = Resolve("ThemeCodeBackgroundBrush", isDark ? "#2A2A2A" : "#F2F2F2");
        var border = Resolve("ThemeControlBorderBrush", isDark ? "#3C3C3C" : "#D0D0D0");
        return (bg, text, codeBg, border);
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void OnWysiwygMarkdownChanged(string markdown)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not MarkdownPreviewViewModel vm) return;
            vm.ApplyWysiwygPageEdit(RewriteImagePaths(markdown, vm.LoadedFilePath, toAbsolute: false));
        });
    }

    private static readonly System.Text.RegularExpressions.Regex ImageRefRegex =
        new(@"!\[([^\]]*)\]\(([^)\s]+)(\s+""[^""]*"")?\)");

    /// <summary>Rewrites relative <c>![alt](path)</c> image references to/from absolute
    /// <c>file:///</c> URIs so the embedded WebView (which has no base path of its own) can
    /// resolve them, while keeping saved markdown portable. Absolute URLs/data URIs pass through.</summary>
    private static string RewriteImagePaths(string markdown, string? loadedFilePath, bool toAbsolute)
    {
        if (string.IsNullOrEmpty(markdown) || string.IsNullOrWhiteSpace(loadedFilePath))
            return markdown;

        var dir = Path.GetDirectoryName(loadedFilePath);
        if (string.IsNullOrEmpty(dir)) return markdown;

        return ImageRefRegex.Replace(markdown, m =>
        {
            var alt = m.Groups[1].Value;
            var path = m.Groups[2].Value;
            var title = m.Groups[3].Value;

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return m.Value;

            if (toAbsolute)
            {
                if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    return m.Value;
                try
                {
                    var full = Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(path)));
                    var uri = new Uri(full).AbsoluteUri;
                    return $"![{alt}]({uri}{title})";
                }
                catch
                {
                    return m.Value;
                }
            }

            if (!path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return m.Value;
            try
            {
                var localPath = new Uri(path).LocalPath;
                var rel = Path.GetRelativePath(dir, localPath).Replace('\\', '/');
                return $"![{alt}]({rel}{title})";
            }
            catch
            {
                return m.Value;
            }
        });
    }

    // ── In-document search ──────────────────────────────────────────────────
    // Operates on the full document (vm.MarkdownText) regardless of the active display mode, so
    // it finds matches outside the currently-shown page too. What happens with a match depends
    // on the mode: Raw Editor gets an exact text selection (ready to type the fix immediately);
    // Rendered/WYSIWYG switch to the page containing it (both already page through the same
    // CurrentPage), with WYSIWYG additionally highlighting the match via the browser's native
    // find inside the WebView (see WysiwygMarkdownEditorControl.FindTextAsync).

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            PerformSearch(backward: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is MarkdownPreviewViewModel vm) vm.CloseSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSearchNextClick(object? sender, RoutedEventArgs e) => PerformSearch(backward: false);
    private void OnSearchPreviousClick(object? sender, RoutedEventArgs e) => PerformSearch(backward: true);

    private void PerformSearch(bool backward)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;
        var query = vm.SearchQuery;
        if (string.IsNullOrWhiteSpace(query))
        {
            vm.SearchStatusText = "";
            return;
        }

        if (query != _lastSearchQuery)
        {
            _lastSearchQuery = query;
            _lastSearchMatchStart = -1;
        }

        var fromIndex = _lastSearchMatchStart >= 0
            ? (backward ? _lastSearchMatchStart : _lastSearchMatchStart + 1)
            : (vm.IsRawEditorMode ? RawEditor.CaretIndex : 0);

        var match = vm.FindInDocument(query, fromIndex, backward);
        if (match is null)
        {
            vm.SearchStatusText = "Няма намерени резултати.";
            _lastSearchMatchStart = -1;
            return;
        }

        var (start, length) = match.Value;
        _lastSearchMatchStart = start;
        vm.SearchStatusText = "";

        if (vm.IsRawEditorMode)
        {
            RawEditor.SelectionStart = start;
            RawEditor.SelectionEnd = start + length;
            RawEditor.CaretIndex = start;
            RawEditor.Focus();
            return;
        }

        var targetPage = vm.GetPageIndexForCharIndex(start);
        if (targetPage != vm.CurrentPage)
            vm.CurrentPage = targetPage;

        if (vm.IsWysiwygMode && _wysiwygControl is IWysiwygEditorControl editor)
            _ = editor.FindTextAsync(query);
    }

    // ── Table of contents: scroll the rendered preview to a clicked heading ─────────────────
    // Markdown.Avalonia tags rendered headings with style classes "Heading1".."Heading6" (see
    // ApplyFontSizeStyles below) — we search for the one whose text matches the TOC entry and
    // scroll it to the top of the preview viewport. Retries a few times since the page may
    // still be (re)rendering right after a CurrentPage switch.

    private void ScrollToHeading(HeadingNavRequest request, int attemptsLeft = 15)
    {
        _previewScroll ??= FindPreviewScrollViewer();
        if (_previewScroll is null)
        {
            if (attemptsLeft > 0)
                Dispatcher.UIThread.Post(() => ScrollToHeading(request, attemptsLeft - 1), DispatcherPriority.Background);
            return;
        }

        var target = FindHeadingControl(request.Level, request.Title);
        if (target is null)
        {
            if (attemptsLeft > 0)
                Dispatcher.UIThread.Post(() => ScrollToHeading(request, attemptsLeft - 1), DispatcherPriority.Background);
            return;
        }

        var point = target.TranslatePoint(new Point(0, 0), _previewScroll);
        if (point is null) return;

        var y = Math.Max(0, _previewScroll.Offset.Y + point.Value.Y - 10);
        _previewScroll.Offset = new Vector(_previewScroll.Offset.X, y);
    }

    private void FindInWysiwyg(string text)
    {
        if (_wysiwygControl is IWysiwygEditorControl editor)
            _ = editor.FindTextAsync(text);
    }

    private void GoToMark(MarkdownPreviewViewModel vm, MarkItem mark)
    {
        if (vm.IsWysiwygMode)
        {
            FindInWysiwyg(mark.Text);
        }
        else if (vm.IsRawEditorMode)
        {
            var text = RawEditor.Text ?? "";
            var index = -1;
            for (int i = 0; i <= mark.Occurrence; i++)
            {
                index = text.IndexOf(mark.Source, index + 1, StringComparison.Ordinal);
                if (index < 0) return;
            }
            RawEditor.SelectionStart = index;
            RawEditor.SelectionEnd = index + mark.Source.Length;
            RawEditor.CaretIndex = index;
            RawEditor.Focus();
        }
        else
        {
            Dispatcher.UIThread.Post(() => ScrollToText(mark.Text), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Rendered-mode counterpart of the WYSIWYG find: scrolls to the first text block on
    /// the page containing <paramref name="text"/>, retrying while the page (re)renders.</summary>
    private void ScrollToText(string text, int attemptsLeft = 15)
    {
        _previewScroll ??= FindPreviewScrollViewer();
        var target = _previewScroll is null ? null : MarkViewer.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => GetHeadingText(c) is { } t && NormalizeHeadingText(t).Contains(text, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            if (attemptsLeft > 0)
                Dispatcher.UIThread.Post(() => ScrollToText(text, attemptsLeft - 1), DispatcherPriority.Background);
            return;
        }

        var point = target.TranslatePoint(new Point(0, 0), _previewScroll!);
        if (point is null) return;
        var y = Math.Max(0, _previewScroll!.Offset.Y + point.Value.Y - 40);
        _previewScroll.Offset = new Vector(_previewScroll.Offset.X, y);
    }

    private Control? FindHeadingControl(int level, string title)
    {
        var normTitle = NormalizeHeadingText(title);
        Control? fallback = null;

        foreach (var desc in MarkViewer.GetVisualDescendants().OfType<Control>())
        {
            if (!desc.Classes.Contains($"Heading{level}")) continue;
            fallback ??= desc;

            var text = GetHeadingText(desc);
            if (text != null && NormalizeHeadingText(text).Equals(normTitle, StringComparison.OrdinalIgnoreCase))
                return desc;
        }
        // No exact text match (inline formatting quirks) — land on the first heading of that
        // level on the page, which is still the right section in the common case.
        return fallback;
    }

    private static string? GetHeadingText(Control control)
    {
        if (control is TextBlock tb) return tb.Text;
        return control.GetType().GetProperty("Text")?.GetValue(control) as string;
    }

    private static string NormalizeHeadingText(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

    private void RestoreOrScrollToPending()
    {
        if (DataContext is MarkdownPreviewViewModel vm && vm.TryGetSavedScrollY(out var absY) && absY > 0)
        {
            // _restoringScroll stays true; ApplyScrollRestore clears it when done.
            AttemptDeferredScrollRestore(absY);
            return;
        }
        if (_pendingScrollParagraph >= 0 && DataContext is MarkdownPreviewViewModel pendingVm)
        {
            ScrollEditorToParagraph(pendingVm, _pendingScrollParagraph);
            _pendingScrollParagraph = -1;
        }
        // Unblock position saves after rendering and scroll events settle.
        Dispatcher.UIThread.Post(() => _restoringScroll = false, DispatcherPriority.Background);
    }

    // Restores the preview (and raw) to the absolute pixel offset `absY`.
    // Defers if the content extent isn't tall enough yet, using two paths:
    //  • LayoutUpdated: fires as content renders — applies as soon as max >= absY.
    //  • Background fallback: safety net for cases where LayoutUpdated stops before
    //    enough content is visible (applies clamped to whatever max is available).
    private void AttemptDeferredScrollRestore(double absY)
    {
        if (_previewScroll is null)
        {
            _pendingScrollY = absY;
            _restoringScroll = true;
            return;
        }

        _pendingScrollY = absY;
        _restoringScroll = true;
        _restoreRetryCount = 0;
        _previewScroll.LayoutUpdated -= OnPreviewScrollLayoutUpdated;
        _previewScroll.LayoutUpdated += OnPreviewScrollLayoutUpdated;
        Dispatcher.UIThread.Post(DoScrollRestoreIfPending, DispatcherPriority.Background);
    }

    // LayoutUpdated path: fires on every layout pass while content is being rendered.
    // Applies as soon as the content above the saved position is rendered (max >= absY).
    // This is correct for absolute offsets: once applied, new content below doesn't move us.
    private void OnPreviewScrollLayoutUpdated(object? sender, EventArgs e)
    {
        if (_previewScroll is null || _pendingScrollY < 0) return;
        var max = _previewScroll.Extent.Height - _previewScroll.Viewport.Height;
        if (max < _pendingScrollY) return;  // not enough content yet, keep waiting
        _previewScroll.LayoutUpdated -= OnPreviewScrollLayoutUpdated;
        var y = _pendingScrollY;
        _pendingScrollY = -1;
        _restoreRetryCount = 0;
        ApplyScrollRestore(y);
    }

    // Background fallback: fires after all pending Render/Layout work.
    // If LayoutUpdated already handled it, _pendingScrollY is -1 and this is a no-op.
    private void DoScrollRestoreIfPending()
    {
        if (_pendingScrollY < 0) return;
        if (_previewScroll is not null)
            _previewScroll.LayoutUpdated -= OnPreviewScrollLayoutUpdated;
        var y = _pendingScrollY;
        _pendingScrollY = -1;
        if (_previewScroll is not null)
        {
            var maxExtent = _previewScroll.Extent.Height - _previewScroll.Viewport.Height;
            if (maxExtent < y && _restoreRetryCount < MaxRestoreRetries)
            {
                _pendingScrollY = y;
                _restoreRetryCount++;
                _previewScroll.LayoutUpdated += OnPreviewScrollLayoutUpdated;
                Dispatcher.UIThread.Post(DoScrollRestoreIfPending, DispatcherPriority.Background);
                return;
            }
        }
        if (_previewScroll is null) return;
        var max = _previewScroll.Extent.Height - _previewScroll.Viewport.Height;
        // Apply clamped: if the window is smaller than when saved, scroll to the maximum reachable
        _restoreRetryCount = 0;
        ApplyScrollRestore(Math.Min(y, Math.Max(max, 0)));
    }

    // Sets the preview offset to absY.
    private void ApplyScrollRestore(double absY)
    {
        if (_previewScroll is null || absY < 0) return;
        _previewScroll.Offset = new Vector(_previewScroll.Offset.X, absY);
        _restoringScroll = false;
    }

    private void ScrollEditorToParagraph(MarkdownPreviewViewModel vm, int paragraphIndex)
    {
        var charIndex = vm.GetRawCharIndexForParagraph(paragraphIndex);
        RawEditor.SelectionStart = charIndex;
        RawEditor.SelectionEnd = charIndex;
        RawEditor.CaretIndex = charIndex;
        RawEditor.Focus();
    }

    private void SavePreviewOffsetIfAvailable()
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;
        if (_previewScroll is null)
            _previewScroll = FindPreviewScrollViewer();
        if (_previewScroll is null) return;
        vm.UpdatePreviewScrollY(_previewScroll.Offset.Y);
    }

    private void HookScrollViewers()
    {
        if (_scrollHooked) return;
        Dispatcher.UIThread.Post(TryHookScrollViewers, DispatcherPriority.Loaded);
    }

    private void TryHookScrollViewers()
    {
        if (_scrollHooked) return;
        _previewScroll = FindPreviewScrollViewer();
        if (_previewScroll == null)
        {
            if (_scrollHookRetryCount++ < MaxScrollHookRetries)
                Dispatcher.UIThread.Post(TryHookScrollViewers, DispatcherPriority.Background);
            return;
        }

        _scrollHookRetryCount = 0;
        _previewScroll.ScrollChanged += OnPreviewScrollChanged;
        _scrollHooked = true;

        // Retry deferred restore that failed earlier because hooks weren't ready
        if (_pendingScrollY >= 0)
            AttemptDeferredScrollRestore(_pendingScrollY);
    }

    private ScrollViewer? FindPreviewScrollViewer()
    {
        return MarkViewer.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
            ?? MarkViewer.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private void UnhookScrollViewers()
    {
        if (!_scrollHooked) return;
        if (_previewScroll != null)
            _previewScroll.ScrollChanged -= OnPreviewScrollChanged;
        _scrollHooked = false;
    }

    private void OnPreviewScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || DataContext is not MarkdownPreviewViewModel vm) return;
        var ratio = GetScrollRatio(sv);
        vm.UpdateLastReadParagraphFromScrollRatio(ratio);
        if (_restoringScroll || !IsVisible) return;
        // Save absolute preview Y — immune to extent differences
        vm.UpdatePreviewScrollY(sv.Offset.Y);
    }

    private static double GetScrollRatio(ScrollViewer sv)
    {
        var max = sv.Extent.Height - sv.Viewport.Height;
        if (max <= 0) return 0;
        return sv.Offset.Y / max;
    }

    private static void SetScrollRatio(ScrollViewer sv, double ratio)
    {
        var max = sv.Extent.Height - sv.Viewport.Height;
        if (max <= 0) return;
        var y = Math.Clamp(ratio, 0, 1) * max;
        sv.Offset = new Vector(sv.Offset.X, y);
    }

    // Inject Avalonia style objects AFTER the MarkdownStyle so they win the cascade.
    // CTextBlock shares TextBlock.FontSizeProperty (via AddOwner), so setting
    // TextBlock.FontSize through a class-based Avalonia style correctly resizes the text.
    private void ApplyFontSizeStyles(double baseSize)
    {
        // Remove old overrides first
        foreach (var s in _fontSizeStyles)
            MarkViewer.Styles.Remove(s);
        _fontSizeStyles.Clear();

        void Add(string cls, double size)
        {
            var style = new Style(sel => sel.Class(cls))
            {
                Setters = { new Setter(TextBlock.FontSizeProperty, size) }
            };
            _fontSizeStyles.Add(style);
            MarkViewer.Styles.Add(style);
        }

        // Body text
        Add("Paragraph",   baseSize);
        Add("Blockquote",  baseSize);
        Add("Note",        baseSize);
        Add("ListMarker",  baseSize);

        // Table cells
        Add("TableHeader", baseSize);
        Add("TableFooter", baseSize);

        // Headings — standard proportional ratios
        Add("Heading1",    Math.Round(baseSize * 2.00));
        Add("Heading2",    Math.Round(baseSize * 1.50));
        Add("Heading3",    Math.Round(baseSize * 1.25));
        Add("Heading4",    Math.Round(baseSize * 1.10));
        Add("Heading5",    baseSize);
        Add("Heading6",    Math.Round(baseSize * 0.90));
    }

    private void UpdateSelectionInVm()
    {
        if (DataContext is MarkdownPreviewViewModel vm)
            vm.SetSelectionStart(RawEditor.SelectionStart);
    }

    private void OnRawEditorPointerReleased(object? sender, PointerReleasedEventArgs e) => UpdateSelectionInVm();
    private void OnRawEditorKeyUp(object? sender, KeyEventArgs e) => UpdateSelectionInVm();

    // Prefer the actual selected text (works for a rendered-preview selection, not just
    // the raw editor's caret position) — falls back to caret position when nothing is selected.
    private async void OnReadFromSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;
        var selectedText = await GetViewerSelectedTextAsync(vm);
        await vm.ReadFromTextAsync(selectedText);
    }

    // Preview right-click menu — copies the current selection to the clipboard.
    private async void OnCopySelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;

        var text = await GetViewerSelectedTextAsync(vm);
        if (string.IsNullOrWhiteSpace(text))
        {
            vm.StatusText = "Няма маркиран текст за копиране.";
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        await clipboard.SetTextAsync(text);
        vm.StatusText = "Текстът е копиран в клипборда.";
    }

    // Chat bubble right-click menu — copies whatever's selected across the chat history;
    // with nothing selected, falls back to copying the whole right-clicked message.
    private async void OnCopyChatSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;

        var text = GetChatSelectedText();
        if (string.IsNullOrWhiteSpace(text) && sender is MenuItem { DataContext: ChatMessage msg })
            text = msg.Content;

        if (string.IsNullOrWhiteSpace(text))
        {
            vm.StatusText = "Няма текст за копиране.";
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        await clipboard.SetTextAsync(text);
        vm.StatusText = "Текстът е копиран в клипборда.";
    }

    /// <summary>Scans the chat history for a text selection — user bubbles use SelectableTextBlock
    /// directly, assistant bubbles render through Markdown.Avalonia's own text control (found via
    /// the same reflection fallback used by GetViewerSelectedTextAsync).</summary>
    private string? GetChatSelectedText()
    {
        var selectableParts = ChatScrollViewer.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Select(stb => stb.SelectedText)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        if (selectableParts.Count > 0)
            return string.Join(" ", selectableParts).Trim();

        foreach (var desc in ChatScrollViewer.GetVisualDescendants())
        {
            var prop = desc.GetType().GetProperty("SelectedText");
            if (prop?.GetValue(desc) is string s && !string.IsNullOrEmpty(s))
                return s.Trim();
        }
        return null;
    }

    private async void OnPasteFromClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null) return;
#pragma warning disable CS0618
        var text = await topLevel.Clipboard.GetTextAsync();
#pragma warning restore CS0618
        if (!string.IsNullOrEmpty(text))
            vm.ChatInput = text;
    }

    // ── Quick-action buttons ────────────────────────────────────────────────
    // These operate on the currently selected text in the preview or raw editor.
    // If ChatInput is non-empty it enriches the prompt as additional instructions.

    private async void OnAddToWordListClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;
        await AddToWordListAsync(vm, await GetViewerSelectedTextAsync(vm));
    }

    private async void OnTranslateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;
        await TranslateAsync(vm, await GetViewerSelectedTextAsync(vm));
    }

    private async void OnExplainClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;
        await ExplainAsync(vm, await GetViewerSelectedTextAsync(vm));
    }

    private async void OnSummarizeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;
        await SummarizeAsync(vm, await GetViewerSelectedTextAsync(vm));
    }

    // Shared by the quick-action buttons above and the WYSIWYG right-click menu.

    private static async Task AddToWordListAsync(MarkdownPreviewViewModel vm, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            vm.StatusText = "Маркирай дума или фраза в прегледа, след което натисни 'Добави дума'.";
            return;
        }
        await vm.AddToWordListAsync(text);
    }

    private static async Task TranslateAsync(MarkdownPreviewViewModel vm, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            vm.StatusText = "Маркирай текст в прегледа или в редактора, след което натисни 'Преведи'.";
            return;
        }
        await vm.TranslateSelectionAsync(text);
    }

    private static async Task ExplainAsync(MarkdownPreviewViewModel vm, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            vm.StatusText = "Маркирай текст в прегледа или в редактора, след което натисни 'Обясни'.";
            return;
        }
        var extra = vm.ChatInput.Trim();
        var prompt = string.IsNullOrEmpty(extra)
            ? $"Обясни следния текст подробно:\n\n{text}"
            : $"Обясни следния текст подробно:\n\n{text}\n\nДопълнителни инструкции: {extra}";
        await vm.ExecuteChatActionAsync(prompt);
    }

    private static async Task SummarizeAsync(MarkdownPreviewViewModel vm, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            vm.StatusText = "Маркирай текст в прегледа или в редактора, след което натисни 'Обобщи'.";
            return;
        }
        var extra = vm.ChatInput.Trim();
        var prompt = string.IsNullOrEmpty(extra)
            ? $"Обобщи следния текст в 2-3 изречения:\n\n{text}"
            : $"Обобщи следния текст в 2-3 изречения:\n\n{text}\n\nДопълнителни инструкции: {extra}";
        await vm.ExecuteChatActionAsync(prompt);
    }

    /// <summary>
    /// Returns the selected text from the active view (preview or raw editor).
    /// Markdown.Avalonia uses CTextBlock internally — we try multiple strategies
    /// to extract the selection reliably.
    /// </summary>
    private async Task<string?> GetViewerSelectedTextAsync(MarkdownPreviewViewModel vm)
    {
        // Raw editor mode — TextBox exposes SelectedText directly.
        if (vm.IsRawEditorMode)
        {
            var sel = RawEditor.SelectedText;
            return string.IsNullOrEmpty(sel) ? null : sel;
        }

        // WYSIWYG mode — ask the embedded editor for its current selection.
        if (vm.IsWysiwygMode)
        {
            if (_wysiwygControl is IWysiwygEditorControl editor)
            {
                var sel = await editor.GetSelectedTextAsync();
                return string.IsNullOrEmpty(sel) ? null : sel;
            }
            return null;
        }

        // Strategy 1: Try SelectableTextBlock descendants (Avalonia built-in)
        var selectableParts = MarkViewer.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Select(stb => stb.SelectedText)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        if (selectableParts.Count > 0)
            return string.Join(" ", selectableParts).Trim();

        // Strategy 2: Try reading SelectedText via reflection on any text control
        // (Markdown.Avalonia's CTextBlock or other custom types)
        var reflectionParts = new List<string>();
        foreach (var desc in MarkViewer.GetVisualDescendants())
        {
            var prop = desc.GetType().GetProperty("SelectedText");
            if (prop?.GetValue(desc) is string s && !string.IsNullOrEmpty(s))
                reflectionParts.Add(s);
        }
        if (reflectionParts.Count > 0)
            return string.Join(" ", reflectionParts).Trim();

        // Strategy 3: Clipboard bridge fallback — simulate Ctrl+C
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null) return null;

#pragma warning disable CS0618
        var previousClipboard = await topLevel.Clipboard.GetTextAsync();
        await topLevel.Clipboard.SetTextAsync(string.Empty);
#pragma warning restore CS0618

        MarkViewer.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = MarkViewer,
            Key = Key.C,
            KeyModifiers = KeyModifiers.Control,
        });

        await Task.Delay(100);

#pragma warning disable CS0618
        var selected = await topLevel.Clipboard.GetTextAsync();
#pragma warning restore CS0618

        if (string.IsNullOrEmpty(selected))
        {
            if (!string.IsNullOrEmpty(previousClipboard))
            {
#pragma warning disable CS0618
                await topLevel.Clipboard.SetTextAsync(previousClipboard);
#pragma warning restore CS0618
            }
            return null;
        }

        return selected.Trim();
    }

    private void OnChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (DataContext is MarkdownPreviewViewModel vm && vm.SendChatCommand.CanExecute(null))
        {
            vm.SendChatCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ScrollPreviewToTop()
    {
        if (_previewScroll is null)
            _previewScroll = FindPreviewScrollViewer();
        if (_previewScroll is not null)
            _previewScroll.Offset = new Vector(_previewScroll.Offset.X, 0);
    }

    private void ScrollChatToBottom()
    {
        ChatScrollViewer.ScrollToEnd();
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;

        // Only re-save directly when LoadedFilePath is a real, reusable OS path. On Browser/Android
        // it may just be a display name with nothing to reopen (Path.IsPathRooted is false then).
        if (!string.IsNullOrEmpty(vm.LoadedFilePath) && Path.IsPathRooted(vm.LoadedFilePath))
        {
            vm.SaveToFile(vm.LoadedFilePath);
            return;
        }

        // No file loaded yet — show Save As picker
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Markdown File",
            SuggestedFileName = "document.md",
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown"] },
                new FilePickerFileType("Text") { Patterns = ["*.txt"] }
            ]
        });

        if (file != null)
            await vm.SaveToFileAsync(file);
    }

    private async void OnExportEpubClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;

        if (string.IsNullOrWhiteSpace(vm.MarkdownText))
        {
            vm.StatusText = "Nothing to export.";
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var baseName = !string.IsNullOrWhiteSpace(vm.LoadedFilePath)
            ? Path.GetFileNameWithoutExtension(vm.LoadedFilePath)
            : "document";

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export EPUB",
            SuggestedFileName = $"{baseName}.epub",
            FileTypeChoices =
            [
                new FilePickerFileType("EPUB") { Patterns = ["*.epub"] }
            ]
        });

        if (file == null)
            return;

        var result = await vm.ExportToEpubAsync(file.Path.LocalPath);
        if (result == null || result.SkippedImages == 0)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select base folder for missing images (optional)",
            AllowMultiple = false
        });

        if (folders.Count == 0)
            return;

        await vm.ExportToEpubAsync(file.Path.LocalPath, new[] { folders[0].Path.LocalPath });
    }
    private async void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Markdown File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown", "*.txt"] },
                new FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (files.Count > 0 && DataContext is MarkdownPreviewViewModel vm)
            await vm.LoadFileAsync(files[0]);
    }

    private async void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;
        // Window(...) requires the classic desktop multi-window platform — constructing one at all
        // throws fatally on Browser/Android (ISingleViewApplicationLifetime has no windowing).
        if (TopLevel.GetTopLevel(this) is not Window parent)
        {
            vm.StatusText = "Историята на файлове не е налична на тази платформа.";
            return;
        }
        var window = new FileHistoryWindow(vm.CacheService);
        await window.ShowDialog(parent);
        if (window.SelectedFilePath != null && File.Exists(window.SelectedFilePath))
            vm.LoadFile(window.SelectedFilePath);
        else if (window.SelectedFilePath != null)
            vm.StatusText = $"Файлът не е намерен: {window.SelectedFilePath}";
    }
}
