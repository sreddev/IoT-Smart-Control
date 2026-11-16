using System.Windows;
using System.Windows.Controls;
using LedvanceControl.App.ViewModels;

namespace LedvanceControl.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            VersionColumn.ItemsSource = vm.Versions;
            VersionCombo.ItemsSource = vm.Versions;
            CategoryCombo.ItemsSource = vm.Categories;
            SecretBox.Password = vm.ClientSecret ?? "";
            StSecretBox.Password = vm.StClientSecret ?? "";
            // a device was added: the form gets a fresh NewDevice, so clear the masked key box too
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SettingsViewModel.NewDevice)) NewKeyBox.Clear(); };
        };
    }

    private void NewKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.NewDevice.LocalKey = NewKeyBox.Password;
    }

    // PasswordBox.Password is not bindable
    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ClientSecret = SecretBox.Password;
    }

    private void StSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.StClientSecret = StSecretBox.Password;
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* no browser available */ }
        e.Handled = true;
    }
}
