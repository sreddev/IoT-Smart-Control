using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using LedvanceControl.App.Mvvm;
using LedvanceControl.Core.Model;
using LedvanceControl.Core.Services;

namespace LedvanceControl.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private DeviceViewModel? _selectedDevice;
    private string _page = "Devices";
    private string _status = "Ready";
    private bool _statusIsError;

    public MainViewModel()
    {
        Manager = new DeviceManager(new ConfigStore());
        Scheduler = new Scheduler(Manager);
        Scheduler.Executed += (e, errors) => Status.Errors($"Schedule '{e.Name}'", errors);
        LedvanceControl.App.Mvvm.Status.Changed += (text, error) => { StatusText = text; StatusIsError = error; };

        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceViewModel.Room)));
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceViewModel.Room), ListSortDirection.Ascending));
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceViewModel.Name), ListSortDirection.Ascending));

        RebuildDevices();
        Groups = new GroupsViewModel(this);
        Scenes = new ScenesViewModel(this);
        Schedules = new SchedulesViewModel(this);
        Settings = new SettingsViewModel(this);

        Manager.ListChanged += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(OnListChanged);
        NavigateCommand = new RelayCommand(p => Page = (string)p!);
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        RefreshAllCommand = new AsyncCommand(RefreshAllAsync);
        AllOffCommand = new AsyncCommand(async () => Status.Errors("All off", await Manager.ForEachAsync(Manager.Devices.Where(d => d.PowerCode != null), d => d.SetPowerAsync(false))));
    }

    public DeviceManager Manager { get; }
    public Scheduler Scheduler { get; }
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ICollectionView DevicesView { get; }
    public GroupsViewModel Groups { get; }
    public ScenesViewModel Scenes { get; }
    public SchedulesViewModel Schedules { get; }
    public SettingsViewModel Settings { get; }
    public ICommand NavigateCommand { get; }
    public ICommand RefreshAllCommand { get; }
    public ICommand AllOffCommand { get; }
    public ICommand ToggleThemeCommand { get; }

    /// <summary>Dark (default) vs Light; persisted and applied immediately.</summary>
    public bool IsDarkTheme
    {
        get => Manager.Config.Theme != ThemeManager.Light;
        set
        {
            var theme = value ? ThemeManager.Dark : ThemeManager.Light;
            if (Manager.Config.Theme == theme) return;
            Manager.Config.Theme = theme;
            Manager.Save();
            ThemeManager.Apply(theme);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeLabel));
        }
    }

    public string ThemeLabel => IsDarkTheme ? "Dark" : "Light";

    public bool HasNoDevices => Devices.Count == 0;

    public string Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            // keep cross-page lists fresh
            if (value == "Scenes") Scenes.Reload();
            if (value == "Schedules") Schedules.Reload();
            if (value == "Groups") Groups.Reload();
            if (value == "Settings") Settings.ReloadDevices();
        }
    }

    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set => Set(ref _selectedDevice, value);
    }

    public string StatusText { get => _status; private set => Set(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public async Task RefreshAllAsync()
    {
        Status.Info("Refreshing devices…");
        var errors = await Manager.ForEachAsync(Manager.Devices, d => d.RefreshAsync());
        var offline = Manager.Devices.Count(d => !d.Online);
        Status.Info($"Refreshed {Manager.Devices.Count()} device(s), {offline} offline" + (errors.Count > 0 ? " – " + string.Join("; ", errors) : ""));
    }

    private void OnListChanged()
    {
        RebuildDevices();
        Scenes.Reload();
        Schedules.Reload();
    }

    private void RebuildDevices()
    {
        var selectedId = SelectedDevice?.Id;
        var existing = Devices.ToDictionary(d => d.Id);
        Devices.Clear();
        foreach (var rt in Manager.Devices)
        {
            // reuse view models whose runtime instance did not change
            var vm = existing.TryGetValue(rt.Id, out var old) && ReferenceEquals(old.Runtime, rt) ? old : new DeviceViewModel(rt);
            if (ReferenceEquals(vm, old)) vm.RebuildDps();
            Devices.Add(vm);
        }
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId) ?? Devices.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoDevices));
    }

    public async ValueTask DisposeAsync()
    {
        Scheduler.Dispose();
        Manager.Save();
        await Manager.DisposeAsync();
    }
}
