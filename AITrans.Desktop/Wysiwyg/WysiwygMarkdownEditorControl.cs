using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private string? _pendingFind;

    // Bumped on every SetMarkdown and stamped by the page onto each md/scroll message it posts.
    // The page debounces edits (400 ms) and scroll reports (250 ms), so a message about the
    // PREVIOUS content can still arrive after new content was pushed (e.g. opening another file
    // right after typing) — without this check it would be merged into the new document.
    private int _generation;

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
        // Loaded from a file rather than HtmlContent: a page handed over as a string has no
        // origin of its own, and WebView2 blocks it from loading file:/// URIs — i.e. every
        // local image the document references (RewriteImagePaths makes them file:/// URIs).
        _webView.Url = new Uri(HtmlFilePath.Value);
    }

    private static readonly Lazy<string> HtmlFilePath = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "AITrans");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "wysiwyg-editor.html");
        File.WriteAllText(path, BuildHtml());
        return path;
    });

    private void OnNavigationCompleted(object? sender, WebViewUrlLoadedEventArg e)
    {
        if (!e.IsSuccess) ErrorOccurred?.Invoke("редакторът не успя да се зареди");
        _ready = true;
        if (_lastMarkdown is { } md)
            PushMarkdown(md.Markdown, md.ScrollY);
        if (_lastStyle is { } style)
            PushStyle(style.Bg, style.Text, style.CodeBg, style.Border, style.FontSizePx);
        if (_pendingFind is { } find)
        {
            _pendingFind = null;
            _ = FindTextAsync(find);
        }
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
            var kind = tProp.GetString();
            if (kind is "md" or "scroll"
                && root.TryGetProperty("g", out var gProp) && gProp.TryGetInt32(out var g)
                && g != _generation)
                return; // stale — about content that has since been replaced
            switch (kind)
            {
                case "md":
                    if (root.TryGetProperty("v", out var vProp))
                    {
                        var edited = RestoreFootnoteDefinitions(vProp.GetString() ?? "");
                        // Keep the copy a NativeControlHost recreation re-pushes in step with the
                        // page — otherwise it would roll the editor back to pre-edit content
                        // while the host already merged the edits.
                        if (_lastMarkdown is { } last) _lastMarkdown = (edited, last.ScrollY);
                        MarkdownChanged?.Invoke(edited);
                    }
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
        _generation++;
        _lastMarkdown = (markdown, scrollY);
        if (_ready) PushMarkdown(markdown, scrollY);
    }

    private void PushMarkdown(string markdown, double scrollY)
    {
        var json = JsonSerializer.Serialize(markdown);
        var scrollArg = scrollY.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ = _webView.ExecuteScriptAsync($"window.setMarkdownFromHost({json}, {scrollArg}, {_generation})");
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

    // Toast UI 2.x has no footnote support: a definition line `[^1]: note.` is shown as plain
    // text and comes back from getMarkdown() escaped as `\[^1\]: note\.` — which would silently
    // break every footnote in a page the moment it's edited here. Undo that escaping on
    // definition lines only (confirmed by round-tripping in the real bundle; footnote
    // references `[^1]` and inline notes `^[...]` already survive untouched).
    private static readonly Regex EscapedFootnoteDefinition =
        new(@"^\\\[\^(?<label>[^\]\r\n]+?)\\\]:(?<rest>.*)$", RegexOptions.Multiline);

    private static readonly Regex MarkdownEscape = new(@"\\([\\`*_{}\[\]()#+\-.!>~|^=])");

    private static string RestoreFootnoteDefinitions(string markdown) =>
        EscapedFootnoteDefinition.Replace(markdown, m =>
            $"[^{MarkdownEscape.Replace(m.Groups["label"].Value, "$1")}]:{MarkdownEscape.Replace(m.Groups["rest"].Value, "$1")}");

    public void InsertText(string text)
    {
        if (!_ready) return;
        _ = _webView.ExecuteScriptAsync($"window.insertTextFromHost({JsonSerializer.Serialize(text)})");
    }

    public void InsertMarkWithNote(string markedText, string label, string note)
    {
        if (!_ready) return;
        var args = string.Join(", ", new[] { markedText, label, note }.Select(s => JsonSerializer.Serialize(s)));
        _ = _webView.ExecuteScriptAsync($"window.markWithNoteFromHost({args})");
    }

    public async Task FindTextAsync(string query)
    {
        if (string.IsNullOrEmpty(query)) return;
        if (!_ready)
        {
            // Freshly created control (e.g. a TOC/mark click that also switched mode) — run it
            // once the page has loaded and received its markdown.
            _pendingFind = query;
            return;
        }
        var json = JsonSerializer.Serialize(query);
        // Deferred a tick so it lands after setMarkdownFromHost's own setTimeout scroll restore.
        await _webView.ExecuteScriptAsync($"setTimeout(function () {{ window.findInEditor({json}); }}, 30)");
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
        <style>
        html, body { margin:0; padding:0; height:100%; } #editor { height:100vh; }
        ::highlight(md-mark) { background-color: rgba(255, 214, 0, 0.45); }
        ::highlight(md-marker) { color: rgba(128, 128, 128, 0.7); }
        ::highlight(md-note) { background-color: rgba(30, 136, 229, 0.18); color: #1E88E5; }
        ::highlight(md-notedef) { color: #1E88E5; }
        .md-tip { position: fixed; z-index: 1000; max-width: 360px; padding: 6px 10px; border-radius: 6px;
                  font: 13px/1.4 sans-serif; white-space: pre-wrap; pointer-events: none; display: none;
                  background: #333; color: #fff; box-shadow: 0 2px 8px rgba(0,0,0,.3); }
        </style>
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

            // Reader marks: `==text==` (highlight), followed by either a footnote reference `[^1]`
            // whose definition `[^1]: note` sits under the paragraph, or (older marks) an inline
            // note `^[note]`. Toast UI 2.x renders all of it as plain text, and anything that
            // changes the editor's DOM to style it (a custom renderer emitting <mark>/<span>) gets
            // mangled by its sanitizer/converter on the way back to markdown — confirmed by
            // round-tripping in the real bundle. The CSS Custom Highlight API paints text ranges
            // without touching the DOM at all, so getMarkdown() stays byte-for-byte identical.
            var markRe = /==([^=\n]+?)==(?:\^\[([^\]\n]*)\]|\[\^([^\]\s]+)\])?/g;
            var noteRe = /\^\[[^\]\n]*\]|\[\^[^\]\s]+\]/g;
            var defRe = /^\[\^([^\]\s]+)\]:\s*([\s\S]*)$/;
            var markInfos = []; // { range, note } — what the hover tooltip looks up
            function refreshHighlights() {
                if (!window.CSS || !CSS.highlights) return;
                var root = document.querySelector('.te-ww-container .tui-editor-contents');
                if (!root) return;
                function range(n, start, end) { var r = new Range(); r.setStart(n, start); r.setEnd(n, end); return r; }

                var defs = {}, defRanges = [];
                // Toast UI 2.x's WYSIWYG paragraphs are top-level <div>s, not <p>s.
                Array.prototype.forEach.call(root.children, function (p) {
                    var d = defRe.exec(p.textContent.trim());
                    if (!d) return;
                    defs[d[1]] = d[2].trim();
                    var r = new Range(); r.selectNodeContents(p); defRanges.push(r);
                });

                var marks = [], markers = [], notes = [];
                markInfos = [];
                var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
                var node, m;
                while ((node = walker.nextNode())) {
                    var s = node.data;
                    markRe.lastIndex = 0;
                    while ((m = markRe.exec(s))) {
                        var end = m.index + 4 + m[1].length;
                        var markRange = range(node, m.index, end);
                        marks.push(markRange);
                        markers.push(range(node, m.index, m.index + 2), range(node, end - 2, end));
                        var note = m[2] !== undefined ? m[2] : (m[3] !== undefined ? defs[m[3]] : null);
                        if (note) markInfos.push({ range: markRange, note: note });
                    }
                    noteRe.lastIndex = 0;
                    while ((m = noteRe.exec(s))) notes.push(range(node, m.index, m.index + m[0].length));
                }
                CSS.highlights.set('md-mark', new Highlight(...marks));
                CSS.highlights.set('md-marker', new Highlight(...markers));
                CSS.highlights.set('md-note', new Highlight(...notes));
                CSS.highlights.set('md-notedef', new Highlight(...defRanges));
            }

            // Hover tooltip with the mark's note. Highlights aren't elements, so there's nothing
            // to hang a title= on — hit-test the pointer against the stored mark ranges instead.
            var tip = document.createElement('div');
            tip.className = 'md-tip';
            document.body.appendChild(tip);
            function hideTip() { tip.style.display = 'none'; }
            document.addEventListener('mousemove', function (e) {
                var hit = null, hitRect = null;
                for (var i = 0; i < markInfos.length && !hit; i++) {
                    var rects = markInfos[i].range.getClientRects();
                    for (var j = 0; j < rects.length; j++) {
                        var r = rects[j];
                        if (e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) {
                            hit = markInfos[i]; hitRect = r; break;
                        }
                    }
                }
                if (!hit) { hideTip(); return; }
                if (tip.textContent !== hit.note) tip.textContent = hit.note;
                tip.style.display = 'block';
                var top = hitRect.top - tip.offsetHeight - 6;
                if (top < 4) top = hitRect.bottom + 6;
                var left = Math.min(Math.max(4, e.clientX - 20), window.innerWidth - tip.offsetWidth - 4);
                tip.style.top = top + 'px';
                tip.style.left = left + 'px';
            });
            document.addEventListener('mouseleave', hideTip);
            document.addEventListener('scroll', hideTip, true);

            // "Маркирай с бележка": `==text==[^label]` in place, and the definition
            // `[^label]: note` as its own paragraph right under the one holding the mark (after
            // any definitions already there) — so the note never interrupts the text itself.
            var defLineRe = /^\\?\[\^[^\]\\]+\\?\]:/;
            window.markWithNoteFromHost = function (text, label, note) {
                var ed = window.__editor;
                ed.focus();
                var ref = '[^' + label + ']';
                ed.insertText('==' + text + '==' + ref);
                var lines = ed.getMarkdown().split('\n');
                var i = -1;
                for (var k = 0; k < lines.length; k++)
                    if (lines[k].indexOf(ref) >= 0 && !defLineRe.test(lines[k])) { i = k; break; }
                if (i < 0) return;
                while (i + 1 < lines.length && lines[i + 1].trim() !== '') i++;
                while (i + 2 < lines.length && lines[i + 1].trim() === '' && defLineRe.test(lines[i + 2])) {
                    i += 2;
                    while (i + 1 < lines.length && lines[i + 1].trim() !== '') i++;
                }
                lines.splice(i + 1, 0, '', ref + ': ' + note);
                var el = getScrollEl();
                var y = el ? el.scrollTop : 0;
                ed.setMarkdown(lines.join('\n'), false);
                setTimeout(function () { if (el) el.scrollTop = y; }, 0);
            };
            var highlightTimer = null;
            function scheduleHighlights() {
                clearTimeout(highlightTimer);
                highlightTimer = setTimeout(refreshHighlights, 120);
            }

            // Ctrl+Shift+H — highlight the selection (single paragraph only; `==` can't span blocks).
            document.addEventListener('keydown', function (e) {
                if (!(e.ctrlKey && e.shiftKey && (e.key === 'H' || e.key === 'h'))) return;
                e.preventDefault();
                var text = window.__editor.getSelectedText();
                if (text && text.indexOf('\n') < 0 && text.indexOf('==') < 0)
                    window.__editor.insertText('==' + text + '==');
            }, true);

            var debounceTimer = null;
            window.__editor.on('change', function () {
                scheduleHighlights();
                if (window.__suppressChange) return;
                clearTimeout(debounceTimer);
                debounceTimer = setTimeout(function () {
                    window.chrome.webview.postMessage(JSON.stringify({ t: 'md', v: window.__editor.getMarkdown(), g: window.__gen }));
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
                    if (el) window.chrome.webview.postMessage(JSON.stringify({ t: 'scroll', y: el.scrollTop, g: window.__gen }));
                }, 250);

                // Mitigates an intermittent WebView2 compositing issue where the content area's
                // themed background goes unpainted (plain white) once scrolling starts, while
                // the static toolbar is unaffected — re-creating the stylesheet node forces a
                // fresh style recalculation/repaint.
                clearTimeout(repaintTimer);
                repaintTimer = setTimeout(reapplyHostStyle, 150);
            }, true); // capture phase — scroll events don't bubble, so listen on the way down

            window.__gen = 0;
            window.setMarkdownFromHost = function (md, scrollY, gen) {
                // Drop any still-pending edit/scroll report about the content being replaced
                // (the host also discards stale ones by generation — see _generation).
                clearTimeout(debounceTimer);
                clearTimeout(scrollPostTimer);
                if (typeof gen === 'number') window.__gen = gen;
                window.__suppressChange = true;
                // cursorToEnd=false — otherwise every page push (including the initial load)
                // jumps the view to the bottom of whatever was just set.
                window.__editor.setMarkdown(md, false);
                scheduleHighlights();
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
                    ' { border-color: ' + s.border + ' !important; }' +
                    '.md-tip { background: ' + s.text + '; color: ' + s.bg + '; }';
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
