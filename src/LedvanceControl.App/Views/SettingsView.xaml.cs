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
        };
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
}
