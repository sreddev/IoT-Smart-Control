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
        var store = new ConfigStore();
        // The Matter fabric (this PC's controller identity + paired nodes) lives next to config.json.
        var matterStorage = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(store.Path)!, "matter");
        Manager = new DeviceManager(store, LedvanceControl.Core.Local.MatterBridgeOptions.Locate(AppContext.BaseDirectory, matterStorage));
        Scheduler = new Scheduler(Manager);
        Scheduler.Executed += (e, errors) => Status.Errors($"Schedule '{e.Name}'", errors);
        LedvanceControl.App.Mvvm.Status.Changed += (text, error) => { StatusText = text; StatusIsError = error; };

        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceViewModel.Room)));
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceViewModel.Room), ListSortDirection.Ascending));
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceViewModel.Name), ListSortDirection.Ascending));
        // The Devices page shows the selected house only (Scenes / Schedules still see every device).
        DevicesView.Filter = o => o is DeviceViewModel d && d.HouseId == Manager.CurrentHouse.Id;

        ReloadHouses();
        RebuildDevices();
        Rooms = new RoomsViewModel(this);
        Groups = new GroupsViewModel(this);
        Scenes = new ScenesViewModel(this);
        Schedules = new SchedulesViewModel(this);
        Settings = new SettingsViewModel(this);

        Manager.ListChanged += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(OnListChanged);
        NavigateCommand = new RelayCommand(p => Page = (string)p!);
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        RefreshAllCommand = new AsyncCommand(RefreshAllAsync);
        AllOffCommand = new AsyncCommand(async () => Status.Errors($"All off in {HomeName}",
            await Manager.ForEachAsync(Manager.DevicesInHouse(Manager.CurrentHouse.Id).Where(d => d.PowerCode != null), d => d.SetPowerAsync(false))));
    }

    /// <summary>Houses for the sidebar switcher.</summary>
    public ObservableCollection<HouseConfig> Houses { get; } = [];

    /// <summary>The selected house; switching it re-scopes the Devices, Rooms and Groups pages.</summary>
    public string CurrentHouseId
    {
        get => Manager.CurrentHouse.Id;
        set
        {
            if (value == null || value == Manager.CurrentHouse.Id) return;
            Manager.SetCurrentHouse(value); // ListChanged → OnListChanged re-scopes the pages
        }
    }

    private void ReloadHouses()
    {
        var names = Manager.Houses.Select(h => (h.Id, h.Name)).ToList();
        if (!names.SequenceEqual(Houses.Select(h => (h.Id, h.Name))))
        {
            Houses.Clear();
            foreach (var h in Manager.Houses) Houses.Add(h);
        }
        OnPropertyChanged(nameof(CurrentHouseId));
        OnPropertyChanged(nameof(HomeName));
        OnPropertyChanged(nameof(HasSeveralHouses));
    }

    public bool HasSeveralHouses => Manager.Houses.Count > 1;

    public DeviceManager Manager { get; }
    public Scheduler Scheduler { get; }
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ICollectionView DevicesView { get; }
    public RoomsViewModel Rooms { get; }
    public GroupsViewModel Groups { get; }

    /// <summary>Name of the selected house (as the SMART+ app shows it on top); managed on the Rooms page.</summary>
    public string HomeName => Manager.CurrentHouse.Name;
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

    /// <summary>Launch at Windows start-up, minimised to the tray; persisted and applied to the Run key.</summary>
    public bool StartMinimized
    {
        get => Manager.Config.StartMinimized;
        set
        {
            if (Manager.Config.StartMinimized == value) return;
            Manager.Config.StartMinimized = value;
            Manager.Save();
            StartupManager.Apply(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Closing the window hides to the tray instead of exiting; persisted.</summary>
    public bool CloseToTray
    {
        get => Manager.Config.CloseToTray;
        set
        {
            if (Manager.Config.CloseToTray == value) return;
            Manager.Config.CloseToTray = value;
            Manager.Save();
            OnPropertyChanged();
        }
    }

    public bool HasNoDevices => !Devices.Any(d => d.HouseId == Manager.CurrentHouse.Id);

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
            if (value == "Rooms") Rooms.Reload();
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
        ReloadHouses();
        RebuildDevices();
        Rooms.Reload();
        Groups.Reload();
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
            var vm = existing.TryGetValue(rt.Id, out var old) && ReferenceEquals(old.Runtime, rt) ? old : new DeviceViewModel(rt, Manager);
            if (ReferenceEquals(vm, old)) vm.RebuildDps();
            Devices.Add(vm);
        }
        var house = Manager.CurrentHouse.Id;
        DevicesView.Refresh();
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId && d.HouseId == house)
                         ?? DevicesView.Cast<DeviceViewModel>().FirstOrDefault();
        OnPropertyChanged(nameof(HasNoDevices));
    }

    public async ValueTask DisposeAsync()
    {
        Scheduler.Dispose();
        Manager.Save();
        await Manager.DisposeAsync();
    }
}
