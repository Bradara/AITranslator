using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using AITrans.ViewModels;

namespace AITrans.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _lastVm;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_lastVm != null)
            _lastVm.PropertyChanged -= OnViewModelPropertyChanged;

        _lastVm = DataContext as SettingsViewModel;

        if (_lastVm != null)
            _lastVm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Close any open ComboBox dropdown before the IsVisible bindings flip,
        // to prevent the Avalonia "PlatformImpl is null" popup warning.
        if (e.PropertyName == nameof(SettingsViewModel.SelectedProvider))
        {
            foreach (var combo in this.GetVisualDescendants().OfType<ComboBox>())
                combo.IsDropDownOpen = false;
        }
    }

    private async void OnPickEbookFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select ebook working folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            vm.EbookWorkingFolder = folders[0].Path.LocalPath;
    }
}
