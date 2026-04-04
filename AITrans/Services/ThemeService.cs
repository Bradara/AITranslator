using System;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace AITrans.Services;

public class ThemeService
{
    private const string ThemeBaseUri = "avares://AITrans/";
    private const string ThemeRootUri = "avares://AITrans/Themes/";

    private IStyle? _currentThemeStyle;

    public void ApplyTheme(string? themeName)
    {
        var app = Application.Current;
        if (app == null) return;

        if (_currentThemeStyle != null)
        {
            app.Styles.Remove(_currentThemeStyle);
            _currentThemeStyle = null;
        }

        var normalized = string.IsNullOrWhiteSpace(themeName) ? "System" : themeName.Trim();
        switch (normalized)
        {
            case "Dark":
                app.RequestedThemeVariant = ThemeVariant.Dark;
                break;
            case "Light":
                app.RequestedThemeVariant = ThemeVariant.Light;
                break;
            case "Dracula":
                ApplyCustomTheme(app, "Dracula.axaml", ThemeVariant.Dark);
                break;
            case "Molokai":
                ApplyCustomTheme(app, "Molokai.axaml", ThemeVariant.Dark);
                break;
            case "Solarized Dark":
                ApplyCustomTheme(app, "SolarizedDark.axaml", ThemeVariant.Dark);
                break;
            case "Solarized Light":
                ApplyCustomTheme(app, "SolarizedLight.axaml", ThemeVariant.Light);
                break;
            case "Papyrus":
                ApplyCustomTheme(app, "Papyrus.axaml", ThemeVariant.Light);
                break;
            case "Papyrus Contrast":
                ApplyCustomTheme(app, "PapyrusContrast.axaml", ThemeVariant.Light);
                break;
            case "Sand":
                ApplyCustomTheme(app, "Sand.axaml", ThemeVariant.Light);
                break;
            case "System":
            default:
                app.RequestedThemeVariant = ThemeVariant.Default;
                break;
        }
    }

    private void ApplyCustomTheme(Application app, string themeFile, ThemeVariant variant)
    {
        app.RequestedThemeVariant = variant;

        var style = new StyleInclude(new Uri(ThemeBaseUri))
        {
            Source = new Uri($"{ThemeRootUri}{themeFile}")
        };

        app.Styles.Add(style);
        _currentThemeStyle = style;
    }
}
