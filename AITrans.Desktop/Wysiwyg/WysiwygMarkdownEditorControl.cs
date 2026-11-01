using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using AvaloniaWebView;
using WebViewCore.Events;
using AITrans.Services;

namespace AITrans.Desktop.Wysiwyg;

/// <summary>Desktop-only WYSIWYG markdown editor: hosts a <see cref="WebView"/> running the
/// vendored Toast UI Editor (MIT, bundled offline — see Wysiwyg/Assets). Registered into
/// <see cref="WysiwygEditorProvider"/> from Program.cs so the shared Preview view can use it
/// without referencing any WebView package.</summary>
public sealed class WysiwygMarkdownEditorControl : UserControl, IWysiwygEditorControl
{
    private readonly WebView _webView;
    private bool _ready;

    // The last markdown/style this control was told to show — not just a "pending, apply once
    // ready" queue: every OnNavigationCompleted (the first one, or any later one forced by a
    // NativeControlHost recreation — see the WebViewCreating handler below) reapplies whichever
    // of these is set, regardless of whether a SetMarkdown/ApplyHostStyle call happened to land
    // while ready or not. That sidesteps a race a one-shot pending queue can't: if a recreation
    // starts AFTER the caller's SetMarkdown already went through (so _ready looked true at the
    // time), a one-shot queue would never re-send it, leaving the fresh page blank.
    private (string Markdown, double ScrollY)? _lastMarkdown;
    private (string Bg, string Text, string CodeBg, string Border, double FontSizePx)? _lastStyle;

    public event Action<string>? MarkdownChanged;
    public event Action<double>? ScrollChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<string, double, double>? ContextMenuRequested;

    public WysiwygMarkdownEditorControl()
    {
        _webView = new WebView();
        Content = _webView;

        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.WebMessageReceived += OnWebMessageReceived;
        // WebView wraps a NativeControlHost, which Avalonia can destroy and recreate the native
        // handle for whenever this control's visibility/attachment changes (e.g. switching away
        // from and back to WYSIWYG mode toggles WysiwygHost's IsVisible) — a known
        // NativeControlHost limitation, not something this package opts out of. That tears down
        // the underlying WebView2 instance and silently starts a fresh one navigating back to
        // BuildHtml() from scratch, so _ready must drop back to false here — otherwise the next
        // SetMarkdown/ApplyHostStyle call after such a recreation thinks the (new, blank) page is
        // already loaded and pushes straight through ExecuteScriptAsync, which just no-ops against
        // a not-yet-initialized platform webview instead of queuing like it should.
        _webView.WebViewCreating += (_, _) => _ready = false;
        _webView.WebViewCreated += (_, e) =>
        {
            if (!e.IsSucceed) ErrorOccurred?.Invoke($"WebView2 не стартира: {e.Message}");
        };

        // Harmlessly ignored until the control attaches to the visual tree (WebView replays
        // Navigate/NavigateToString against the then-current property value on attach).
        _webView.HtmlContent = BuildHtml();
    }

    private void OnNavigationCompleted(object? sender, WebViewUrlLoadedEventArg e)
    {
        if (!e.IsSuccess) ErrorOccurred?.Invoke("редакторът не успя да се зареди");
        _ready = true;
        if (_lastMarkdown is { } md)
            PushMarkdown(md.Markdown, md.ScrollY);
        if (_lastStyle is { } style)
            PushStyle(style.Bg, style.Text, style.CodeBg, style.Border, style.FontSizePx);
    }

    // The page posts a small JSON envelope — {"t":"md","v":...} for edits, {"t":"scroll","y":...}
    // for scroll position — over the one message channel WebView2 exposes, rather than using
    // separate channels (there is only the one). This runs on WebView2's own message-pump
    // callback, not the UI thread's normal exception-handling path, so anything this throws is
    // liable to take down more than just this handler — every branch below is deliberately
    // defensive (TryGetProperty, not GetProperty, and a catch-all) rather than trusting the
    // envelope's shape.
    private void OnWebMessageReceived(object? sender, WebViewMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.Message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var tProp)) return;
            switch (tProp.GetString())
            {
                case "md":
                    if (root.TryGetProperty("v", out var vProp))
                        MarkdownChanged?.Invoke(vProp.GetString() ?? "");
                    break;
                case "scroll":
                    if (root.TryGetProperty("y", out var yProp) && yProp.TryGetDouble(out var y))
                        ScrollChanged?.Invoke(y);
                    break;
                case "contextmenu":
                {
                    var menuX = root.TryGetProperty("x", out var xProp) && xProp.TryGetDouble(out var xv) ? xv : 0;
                    var menuY = root.TryGetProperty("y", out var yProp2) && yProp2.TryGetDouble(out var yv) ? yv : 0;
                    var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
                    ContextMenuRequested?.Invoke(text, menuX, menuY);
                    break;
                }
                case "jserror":
                {
                    var where = root.TryGetProperty("where", out var w) ? w.GetString() : "?";
                    var message = root.TryGetProperty("message", out var m) ? m.GetString() : "?";
                    ErrorOccurred?.Invoke($"JS грешка ({where}): {message}");
                    break;
                }
            }
        }
        catch
        {
            // Malformed/unexpected message — ignore rather than risk an unhandled exception on
            // WebView2's own callback thread.
        }
    }

    public void SetMarkdown(string markdown, double scrollY = 0)
    {
        _lastMarkdown = (markdown, scrollY);
        if (_ready) PushMarkdown(markdown, scrollY);
    }

    private void PushMarkdown(string markdown, double scrollY)
    {
        var json = JsonSerializer.Serialize(markdown);
        var scrollArg = scrollY.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ = _webView.ExecuteScriptAsync($"window.setMarkdownFromHost({json}, {scrollArg})");
    }

    public async Task<string?> GetSelectedTextAsync()
    {
        if (!_ready) return null;
        var result = await _webView.ExecuteScriptAsync(
            "window.__editor ? window.__editor.getSelectedText() : ''");
        if (string.IsNullOrEmpty(result)) return null;
        try { return JsonSerializer.Deserialize<string>(result); }
        catch { return result; }
    }

    public void InsertText(string text)
    {
        if (!_ready) return;
        _ = _webView.ExecuteScriptAsync($"window.insertTextFromHost({JsonSerializer.Serialize(text)})");
    }

    public async Task FindTextAsync(string query)
    {
        if (!_ready || string.IsNullOrEmpty(query)) return;
        var json = JsonSerializer.Serialize(query);
        await _webView.ExecuteScriptAsync($"window.findInEditor({json})");
    }

    public void ApplyHostStyle(string backgroundHex, string textHex, string codeBackgroundHex, string borderHex, double fontSizePx)
    {
        _lastStyle = (backgroundHex, textHex, codeBackgroundHex, borderHex, fontSizePx);
        if (_ready) PushStyle(backgroundHex, textHex, codeBackgroundHex, borderHex, fontSizePx);
    }

    private void PushStyle(string bg, string text, string codeBg, string border, double fontSizePx)
    {
        var args = string.Join(", ", new[] { bg, text, codeBg, border }.Select(s => JsonSerializer.Serialize(s)))
                   + $", {fontSizePx.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        _ = _webView.ExecuteScriptAsync($"window.setStyleFromHost({args})");
    }

    // ── HTML harness ─────────────────────────────────────────────────────────
    // The vendored Toast UI/CodeMirror js/css are base64-inlined (so embedding can't collide
    // with a literal "</script" inside the minified bundles' own HTML-sanitizer regexes, which
    // would otherwise prematurely terminate the surrounding <script> tag) and executed by
    // creating real <script> elements rather than eval().
    //
    // Pinned to Toast UI Editor v2.5.4, not the newer v3.x: v3's UMD build externalizes
    // ProseMirror via `root[undefined]` in its plain-<script> fallback branch (upstream bug —
    // confirmed by extracting and running the exact bundle in Chrome: `toastui.Editor` never
    // becomes a constructor, so the editor silently never loads). v2.5.4's UMD build only
    // externalizes CodeMirror via a proper `root["CodeMirror"]` reference, which we vendor and
    // load first — confirmed working standalone, including setMarkdown round-tripping.

    private static string BuildHtml()
    {
        var jsB64 = Convert.ToBase64String(ReadEmbeddedBytes("toastui-editor.min.js"));
        var cssB64 = Convert.ToBase64String(ReadEmbeddedBytes("toastui-editor.min.css"));
        var cmB64 = Convert.ToBase64String(ReadEmbeddedBytes("codemirror.min.js"));

        return $$"""
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8" />
        <style>html, body { margin:0; padding:0; height:100%; } #editor { height:100vh; }</style>
        </head>
        <body>
        <div id="editor"></div>
        <script>
        (function () {
            // ExecuteScriptAsync doesn't distinguish a script that threw from one that succeeded
            // (its return value is just whatever the expression evaluated to), so route JS
            // exceptions back over the message channel — surfaced as ErrorOccurred.
            function reportJsError(where, err) {
                try {
                    window.chrome.webview.postMessage(JSON.stringify({
                        t: 'jserror',
                        where: where,
                        message: (err && err.message) ? err.message : String(err),
                        stack: (err && err.stack) ? err.stack : ''
                    }));
                } catch (e) { /* even reporting failed — nothing more we can do from here */ }
            }
            window.onerror = function (message, source, lineno, colno, error) {
                reportJsError('window.onerror', error || message);
            };

            try {
            var styleEl = document.createElement('style');
            styleEl.appendChild(document.createTextNode(atob("{{cssB64}}")));
            document.head.appendChild(styleEl);

            var cmEl = document.createElement('script');
            cmEl.text = atob("{{cmB64}}");
            document.head.appendChild(cmEl);

            var scriptEl = document.createElement('script');
            scriptEl.text = atob("{{jsB64}}");
            document.head.appendChild(scriptEl);

            window.__suppressChange = false;
            window.__editor = new toastui.Editor({
                el: document.querySelector('#editor'),
                height: '100%',
                initialEditType: 'wysiwyg',
                hideModeSwitch: true,
                usageStatistics: false
            });

            var debounceTimer = null;
            window.__editor.on('change', function () {
                if (window.__suppressChange) return;
                clearTimeout(debounceTimer);
                debounceTimer = setTimeout(function () {
                    window.chrome.webview.postMessage(JSON.stringify({ t: 'md', v: window.__editor.getMarkdown() }));
                }, 400);
            });

            // .te-editor is the content area's actual scroll container (confirmed by direct
            // testing — scrollTop moves on it, not on .tui-editor-contents, its contenteditable
            // child, despite both reporting the same scrollHeight). Toast UI keeps a SECOND,
            // hidden .te-editor around for its (unused — hideModeSwitch:true) markdown-source
            // mode, so a bare `.te-editor` selector can silently grab the wrong, always-0 one;
            // scoping to `.te-ww-container .te-editor` (the WYSIWYG-mode wrapper) gets the real
            // one — confirmed by direct testing in Chrome.
            var scrollEl = null;
            function getScrollEl() {
                if (!scrollEl) scrollEl = document.querySelector('.te-ww-container .te-editor');
                return scrollEl;
            }

            var scrollPostTimer = null;
            document.addEventListener('scroll', function () {
                clearTimeout(scrollPostTimer);
                scrollPostTimer = setTimeout(function () {
                    var el = getScrollEl();
                    if (el) window.chrome.webview.postMessage(JSON.stringify({ t: 'scroll', y: el.scrollTop }));
                }, 250);

                // Mitigates an intermittent WebView2 compositing issue where the content area's
                // themed background goes unpainted (plain white) once scrolling starts, while
                // the static toolbar is unaffected — re-creating the stylesheet node forces a
                // fresh style recalculation/repaint.
                clearTimeout(repaintTimer);
                repaintTimer = setTimeout(reapplyHostStyle, 150);
            }, true); // capture phase — scroll events don't bubble, so listen on the way down

            window.setMarkdownFromHost = function (md, scrollY) {
                window.__suppressChange = true;
                // cursorToEnd=false — otherwise every page push (including the initial load)
                // jumps the view to the bottom of whatever was just set.
                window.__editor.setMarkdown(md, false);
                if (scrollY) {
                    setTimeout(function () {
                        var el = getScrollEl();
                        if (el) el.scrollTop = scrollY;
                    }, 0);
                } else {
                    window.__editor.moveCursorToStart();
                }
                setTimeout(function () { window.__suppressChange = false; }, 0);
            };

            // window.find() selects the match (confirmed via Selection API) but does not by
            // itself scroll it into view, since the WYSIWYG content area is its own internal
            // overflow:auto container, not the top-level document — hence the explicit
            // scrollIntoView on the match's element (confirmed by direct testing in Chrome).
            window.findInEditor = function (query) {
                var found = window.find(query, false, false, true, false, true, false);
                if (!found) return false;
                var sel = window.getSelection();
                if (sel.rangeCount === 0) return found;
                var node = sel.anchorNode;
                var el = node && node.nodeType === 3 ? node.parentElement : node;
                if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center' });
                return found;
            };

            // Right-click: suppress the browser's own menu and hand the click position (CSS px,
            // relative to the page — equal to Avalonia DIPs, since WebView2 applies the same DPI
            // scaling) plus the current selection to the host, which shows the app's own menu.
            // The selection is captured now because opening that menu moves focus out of the page.
            document.addEventListener('contextmenu', function (e) {
                e.preventDefault();
                var text = '';
                try { text = window.__editor.getSelectedText() || ''; } catch (err) { }
                if (!text) text = String(window.getSelection() || '');
                window.chrome.webview.postMessage(JSON.stringify({ t: 'contextmenu', x: e.clientX, y: e.clientY, text: text }));
            });

            // Replaces the current selection (or inserts at the caret) — used for the host menu's
            // Paste (clipboard text) and Cut (empty string). focus() first restores the selection
            // the editor remembered when the host menu took focus away.
            window.insertTextFromHost = function (text) {
                window.__editor.focus();
                window.__editor.insertText(text);
            };

            // Toast UI Editor v2.x's DOM classes are prefixed "tui-editor-" (not "toastui-
            // editor-" — that prefix only applies from v3 onward, which we can't use here; see
            // the version note above). Confirmed by inspecting the live DOM in Chrome, same as
            // the rest of this harness. There is no bundled dark theme for v2.x, so the host
            // pushes its own theme colors here instead.
            //
            // .te-ww-container and .te-editor also need covering, not just .tui-editor-contents:
            // the bundled CSS hard-codes `.te-ww-container{background-color:#fff}` on the
            // container that sits directly behind the scrollable content area (confirmed by
            // grepping the vendored stylesheet) — left unstyled, it's a plain white rect right
            // behind/around the themed content.
            var lastStyle = null;
            var repaintTimer = null;

            function buildHostStyleCss(s) {
                return 'html, body { background: ' + s.bg + ' !important; }' +
                    '.tui-editor-defaultUI, .te-ww-container, .te-editor { background: ' + s.bg + ' !important; border-color: ' + s.border + ' !important; }' +
                    '.tui-editor-defaultUI-toolbar { background: ' + s.bg + ' !important; border-color: ' + s.border + ' !important; }' +
                    '.tui-editor-contents { background: ' + s.bg + ' !important; color: ' + s.text + ' !important; font-size: ' + s.fontSizePx + 'px !important; }' +
                    '.tui-editor-contents h1, .tui-editor-contents h2, .tui-editor-contents h3,' +
                    '.tui-editor-contents h4, .tui-editor-contents h5, .tui-editor-contents h6,' +
                    '.tui-editor-contents p, .tui-editor-contents li, .tui-editor-contents td, .tui-editor-contents th' +
                    ' { color: ' + s.text + ' !important; }' +
                    '.tui-editor-contents blockquote { color: ' + s.text + ' !important; border-left-color: ' + s.border + ' !important; }' +
                    '.tui-editor-contents code, .tui-editor-contents pre { background: ' + s.codeBg + ' !important; color: ' + s.text + ' !important; }' +
                    '.tui-editor-contents hr, .tui-editor-contents table, .tui-editor-contents th, .tui-editor-contents td' +
                    ' { border-color: ' + s.border + ' !important; }';
            }

            // Recreates the <style> node rather than mutating one in place — more reliably
            // forces Chromium to recompute/repaint than reassigning textContent on the same node
            // (used both for a real style change and as the scroll-triggered repaint nudge above).
            function reapplyHostStyle() {
                if (!lastStyle) return;
                var old = document.getElementById('host-style');
                if (old) old.remove();
                var el = document.createElement('style');
                el.id = 'host-style';
                el.textContent = buildHostStyleCss(lastStyle);
                document.head.appendChild(el);
            }

            window.setStyleFromHost = function (bg, text, codeBg, border, fontSizePx) {
                lastStyle = { bg: bg, text: text, codeBg: codeBg, border: border, fontSizePx: fontSizePx };
                reapplyHostStyle();
            };

            } catch (err) {
                reportJsError('bootstrap', err);
            }
        })();
        </script>
        </body>
        </html>
        """;
    }

    private static byte[] ReadEmbeddedBytes(string fileName)
    {
        var asm = typeof(WysiwygMarkdownEditorControl).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .First(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var mem = new MemoryStream();
        stream.CopyTo(mem);
        return mem.ToArray();
    }
}
