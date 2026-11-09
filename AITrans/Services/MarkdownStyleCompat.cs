using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using ColorDocument.Avalonia;
using Markdown.Avalonia;

namespace AITrans.Services;

/// <summary>Works around a Markdown.Avalonia 11.0.3-a1 / Avalonia 11.3 incompatibility: the
/// library's code-block and inline-code foreground (its DivideColorExtension) is a MultiBinding
/// whose literal-color input is the library's own internal StaticBinding — a bare IBinding, which
/// Avalonia 11.3's MultiBindingExpression rejects with NotSupportedException the first time any
/// page containing code gets laid out, crashing the app. Before a viewer renders anything, this
/// swaps each such StaticBinding for a standard <see cref="Binding"/> with the same constant
/// value, so the look stays identical.</summary>
public static class MarkdownStyleCompat
{
    private const string StaticBindingTypeName = "Markdown.Avalonia.Extensions.StaticBinding";
    private static readonly ConditionalWeakTable<IStyle, object> Patched = new();
    private static bool _registered;

    /// <summary>Call once at startup — patches every MarkdownScrollViewer's style set as soon as
    /// it's handed markdown, i.e. before its content is ever measured.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        MarkdownScrollViewer.MarkdownProperty.Changed.AddClassHandler<MarkdownScrollViewer>((viewer, _) => Patch(viewer));
        MarkdownScrollViewer.SourceProperty.Changed.AddClassHandler<MarkdownScrollViewer>((viewer, _) => Patch(viewer));
        InputElement.PointerPressedEvent.AddClassHandler<MarkdownScrollViewer>(GuardSelectionStart, RoutingStrategies.Tunnel);
    }

    private static readonly FieldInfo? DocumentField =
        typeof(MarkdownScrollViewer).GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Second incompatibility, in ColorDocument.Avalonia (the selection engine under
    /// SelectionEnabled="True"): a click starts a selection by measuring every document block's
    /// position, and any block that hasn't been arranged yet (e.g. a click right after a page
    /// change, before layout catches up) is left as an empty entry that it then dereferences —
    /// NullReferenceException, crashing the app. Runs on the tunnel pass, ahead of the library's
    /// own handler: flush pending layout first, and if a block is still unarranged, swallow this
    /// one click instead of letting the selection start.</summary>
    private static void GuardSelectionStart(MarkdownScrollViewer viewer, PointerPressedEventArgs e)
    {
        if (!viewer.SelectionEnabled || !e.GetCurrentPoint(viewer).Properties.IsLeftButtonPressed) return;
        if (DocumentField?.GetValue(viewer) is not DocumentElement document) return;
        if (AllArranged(document)) return;
        viewer.UpdateLayout();
        if (!AllArranged(document)) e.Handled = true;
    }

    private static bool AllArranged(DocumentElement element)
    {
        foreach (var child in element.Children)
        {
            if (child is null || LayoutInformation.GetPreviousArrangeBounds(child.Control) is null)
                return false;
            if (!AllArranged(child)) return false;
        }
        return true;
    }

    private static void Patch(MarkdownScrollViewer viewer)
    {
        var style = viewer.MarkdownStyle;
        if (style is null || Patched.TryGetValue(style, out _)) return;
        PatchStyle(style);
        Patched.AddOrUpdate(style, new object());
    }

    private static void PatchStyle(IStyle style)
    {
        if (style is Style s)
        {
            foreach (var setterBase in s.Setters)
                if (setterBase is Setter { Value: MultiBinding multi })
                    PatchMultiBinding(multi);
        }

        IEnumerable<IStyle>? children = style switch
        {
            Styles styles => styles,
            StyleBase styleBase => styleBase.Children,
            _ => null
        };
        if (children is null) return;
        foreach (var child in children)
            PatchStyle(child);
    }

    private static void PatchMultiBinding(MultiBinding multi)
    {
        var patched = false;
        for (var i = 0; i < multi.Bindings.Count; i++)
        {
            var binding = multi.Bindings[i];
            if (binding?.GetType().FullName != StaticBindingTypeName) continue;
            var value = binding.GetType()
                .GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(binding);
            multi.Bindings[i] = new Binding { Source = value };
            patched = true;
        }

        if (patched && multi.Converter is { } inner and not BrushResultConverter)
            multi.Converter = new BrushResultConverter(inner);
    }

    /// <summary>The library's DivideConverter falls back to returning its first input as-is when
    /// the second isn't a color yet (e.g. a DynamicResource that hasn't resolved) — and that
    /// first input is a raw <see cref="Color"/>, which Avalonia can't assign to the brush
    /// property being set (InvalidCastException). Turn any such Color into a brush.</summary>
    private sealed class BrushResultConverter(IMultiValueConverter inner) : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            inner.Convert(values, targetType, parameter, culture) switch
            {
                Color color => new SolidColorBrush(color),
                var result => result
            };
    }
}
