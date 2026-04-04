using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using AITrans.ViewModels;

namespace AITrans.Views;

public partial class MarkdownPreviewView : UserControl
{
    // Tracking styles we inject so we can remove them before updating
    private readonly List<Style> _fontSizeStyles = [];
    private int _pendingScrollParagraph = -1;
    private MarkdownPreviewViewModel? _subscribedVm;
    private ScrollViewer? _rawScroll;
    private ScrollViewer? _previewScroll;
    private bool _syncingScroll;
    private bool _scrollHooked;
    private int _scrollHookRetryCount;
    private const int MaxScrollHookRetries = 30;
    // Deferred scroll restore — negative means nothing pending
    private double _pendingScrollY = -1;
    private int _restoreRetryCount;
    private const int MaxRestoreRetries = 30;
    private bool _restoringScroll;

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
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        _subscribedVm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;

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
        HookScrollViewers();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;

        if (change.GetNewValue<bool>())
        {
            HookScrollViewers(); // Ensure hooks are set up when tab becomes visible
            if (DataContext is MarkdownPreviewViewModel vm)
                vm.RequestRestoreScroll();
            Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.Root is Window win)
            win.Closing += OnWindowClosing;
        if (DataContext is MarkdownPreviewViewModel vm)
            vm.RequestRestoreScroll();
        Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
        HookScrollViewers();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (e.Root is Window win)
            win.Closing -= OnWindowClosing;
        SavePreviewOffsetIfAvailable();
        if (DataContext is MarkdownPreviewViewModel vm)
            vm.PersistSessionState();
        UnhookScrollViewers();
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
        }

        // Rebuild font-size override styles when the user clicks A+/A-
        if (pe.PropertyName == nameof(MarkdownPreviewViewModel.PreviewFontSize))
            ApplyFontSizeStyles(vm.PreviewFontSize);

        if (pe.PropertyName == nameof(MarkdownPreviewViewModel.ScrollToParagraph) && vm.ScrollToParagraph >= 0)
        {
            _pendingScrollParagraph = vm.ScrollToParagraph;
            vm.ScrollToParagraph = -1; // consume the signal
            if (IsVisible)
                Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
        }
    }

    private void RestoreOrScrollToPending()
    {
        if (DataContext is MarkdownPreviewViewModel vm && vm.TryGetSavedScrollY(out var absY) && absY > 0)
        {
            AttemptDeferredScrollRestore(absY);
            return;
        }
        if (_pendingScrollParagraph < 0 || DataContext is not MarkdownPreviewViewModel pendingVm) return;
        ScrollEditorToParagraph(pendingVm, _pendingScrollParagraph);
        _pendingScrollParagraph = -1;
    }

    // Restores the preview (and raw) to the absolute pixel offset `absY`.
    // Defers if the content extent isn't tall enough yet, using two paths:
    //  • LayoutUpdated: fires as content renders — applies as soon as max >= absY.
    //  • Background fallback: safety net for cases where LayoutUpdated stops before
    //    enough content is visible (applies clamped to whatever max is available).
    private void AttemptDeferredScrollRestore(double absY)
    {
        if (_previewScroll is null || _rawScroll is null)
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

    // Sets the preview offset to absY and syncs the raw editor via ratio.
    private void ApplyScrollRestore(double absY)
    {
        if (_previewScroll is null || _rawScroll is null || absY < 0) return;
        _syncingScroll = true;
        _previewScroll.Offset = new Vector(_previewScroll.Offset.X, absY);
        var ratio = GetScrollRatio(_previewScroll);
        SetScrollRatio(_rawScroll, ratio);
        _syncingScroll = false;
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
        _rawScroll = RawEditor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        _previewScroll = FindPreviewScrollViewer();
        if (_rawScroll == null || _previewScroll == null)
        {
            if (_scrollHookRetryCount++ < MaxScrollHookRetries)
                Dispatcher.UIThread.Post(TryHookScrollViewers, DispatcherPriority.Background);
            return;
        }

        _scrollHookRetryCount = 0;
        _rawScroll.ScrollChanged += OnRawScrollChanged;
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
        if (_rawScroll != null)
            _rawScroll.ScrollChanged -= OnRawScrollChanged;
        if (_previewScroll != null)
            _previewScroll.ScrollChanged -= OnPreviewScrollChanged;
        _scrollHooked = false;
    }

    private void OnRawScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        if (sender is not ScrollViewer sv || DataContext is not MarkdownPreviewViewModel vm) return;
        var ratio = GetScrollRatio(sv);
        SyncScrollToRatio(ratio, sourceIsRaw: true);
        vm.UpdateLastReadParagraphFromScrollRatio(ratio);
        // Save the preview's resulting absolute Y (not the raw ratio)
        if (_restoringScroll || !IsVisible) return;
        if (_previewScroll is not null)
            vm.UpdatePreviewScrollY(_previewScroll.Offset.Y);
    }

    private void OnPreviewScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        if (sender is not ScrollViewer sv || DataContext is not MarkdownPreviewViewModel vm) return;
        var ratio = GetScrollRatio(sv);
        SyncScrollToRatio(ratio, sourceIsRaw: false);
        vm.UpdateLastReadParagraphFromScrollRatio(ratio);
        if (_restoringScroll || !IsVisible) return;
        // Save absolute preview Y — immune to extent differences
        vm.UpdatePreviewScrollY(sv.Offset.Y);
    }
    private void SyncScrollToRatio(double ratio, bool? sourceIsRaw = null)
    {
        if (_rawScroll == null || _previewScroll == null) return;
        _syncingScroll = true;
        if (sourceIsRaw != false)
            SetScrollRatio(_previewScroll, ratio);
        if (sourceIsRaw != true)
            SetScrollRatio(_rawScroll, ratio);
        _syncingScroll = false;
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
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;

        if (!string.IsNullOrEmpty(vm.LoadedFilePath))
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
            vm.SaveToFile(file.Path.LocalPath);
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
            vm.LoadFile(files[0].Path.LocalPath);
    }
}
