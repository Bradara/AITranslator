using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using AITrans.Services;
using AITrans.ViewModels;
using AITrans.Views;

namespace AITrans;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            var settingsService = new SettingsService();
            settingsService.Load();
            var themeService = new ThemeService();
            themeService.ApplyTheme(settingsService.Settings.ThemeName);
            var mainViewModel = new MainViewModel(settingsService, themeService);
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };
            desktop.Exit += (_, _) => mainViewModel.SaveState();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            var settingsService = new SettingsService();
            settingsService.Load();
            var themeService = new ThemeService();
            themeService.ApplyTheme(settingsService.Settings.ThemeName);
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel(settingsService, themeService)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}