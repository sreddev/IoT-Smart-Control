using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LedvanceControl.App.Mvvm;
using LedvanceControl.Core.Cloud;
using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Model;
using LedvanceControl.Core.Services;

namespace LedvanceControl.App.ViewModels;

// ==================================================================== groups

public sealed class GroupViewModel : ObservableObject, ILightTarget
{
    private readonly DeviceManager _manager;

    public GroupViewModel(DeviceManager manager, GroupConfig config, IEnumerable<DeviceViewModel> allDevices)
    {
        _manager = manager;
        Config = config;
        Light = new LightControlViewModel(this);
        Members = allDevices.Select(d => new MemberItem(d, config.DeviceIds.Contains(d.Id), OnMembershipChanged)).ToList();
        OnCommand = new AsyncCommand(async () => Status.Errors($"{Name} on", await _manager.SetGroupPowerAsync(Config, true)));
        OffCommand = new AsyncCommand(async () => Status.Errors($"{Name} off", await _manager.SetGroupPowerAsync(Config, false)));
    }

    public GroupConfig Config { get; }
    public LightControlViewModel Light { get; }
    public IReadOnlyList<MemberItem> Members { get; }
    public ICommand OnCommand { get; }
    public ICommand OffCommand { get; }
    public string MemberSummary => $"{Config.DeviceIds.Count} device(s)";

    public string Name
    {
        get => Config.Name;
        set
        {
            if (Config.Name == value) return;
            Config.Name = value;
            OnPropertyChanged();
            _manager.Save();
        }
    }

    private void OnMembershipChanged()
    {
        Config.DeviceIds = Members.Where(m => m.IsMember).Select(m => m.Device.Id).ToList();
        _manager.Save();
        OnPropertyChanged(nameof(MemberSummary));
        Light.CapabilitiesChanged();
    }

    private IEnumerable<LightProfile> Profiles => _manager.Members(Config).Select(d => d.Light).OfType<LightProfile>();
    bool ILightTarget.HasTemp => Profiles.Any(p => p.HasTemp);
    bool ILightTarget.HasColour => Profiles.Any(p => p.HasColour);
    bool ILightTarget.HasScenes => Profiles.Any(p => p.HasScenes);

    async Task ILightTarget.SetPowerAsync(bool on) => Status.Errors($"{Name} power", await _manager.SetGroupPowerAsync(Config, on));

    async Task ILightTarget.ApplyAsync(Func<LightProfile, IReadOnlyDictionary<string, object?>> build)
    {
        var errors = await _manager.SetGroupLightAsync(Config, build);
        if (errors.Count > 0) Status.Errors(Name, errors);
    }

    public sealed class MemberItem(DeviceViewModel device, bool isMember, Action changed) : ObservableObject
    {
        private bool _isMember = isMember;
        public DeviceViewModel Device { get; } = device;

        public bool IsMember
        {
            get => _isMember;
            set { if (Set(ref _isMember, value)) changed(); }
        }
    }
}

public sealed class GroupsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private GroupViewModel? _selected;

    public GroupsViewModel(MainViewModel main)
    {
        _main = main;
        AddCommand = new RelayCommand(Add);
        DeleteCommand = new RelayCommand(Delete, _ => Selected != null);
        Reload();
    }

    public ObservableCollection<GroupViewModel> Groups { get; } = [];
    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }

    public GroupViewModel? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public void Reload()
    {
        var id = Selected?.Config.Id;
        Groups.Clear();
        foreach (var g in _main.Manager.Config.Groups) Groups.Add(new GroupViewModel(_main.Manager, g, _main.Devices));
        Selected = Groups.FirstOrDefault(g => g.Config.Id == id) ?? Groups.FirstOrDefault();
    }

    private void Add()
    {
        var g = new GroupConfig { Name = $"Group {Groups.Count + 1}" };
        _main.Manager.Config.Groups.Add(g);
        _main.Manager.Save();
        var vm = new GroupViewModel(_main.Manager, g, _main.Devices);
        Groups.Add(vm);
        Selected = vm;
    }

    private void Delete()
    {
        if (Selected == null) return;
        _main.Manager.Config.Groups.Remove(Selected.Config);
        _main.Manager.Save();
        Groups.Remove(Selected);
        Selected = Groups.FirstOrDefault();
    }
}

// ==================================================================== scenes

public sealed class ScenesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _newSceneName = "My scene";

    public ScenesViewModel(MainViewModel main)
    {
        _main = main;
        RunLocalCommand = new AsyncCommand(async p =>
        {
            var s = (LocalScene)p!;
            Status.Errors($"Scene '{s.Name}'", await _main.Manager.RunSceneAsync(s));
        });
        DeleteLocalCommand = new RelayCommand(p =>
        {
            var s = (LocalScene)p!;
            _main.Manager.Config.Scenes.Remove(s);
            _main.Manager.Config.Schedules.RemoveAll(e => e.TargetType == ScheduleTarget.LocalScene && e.TargetId == s.Id);
            _main.Manager.Save();
            LocalScenes.Remove(s);
        });
        CaptureCommand = new RelayCommand(Capture, _ => _main.Devices.Any(d => d.IsSelected) && !string.IsNullOrWhiteSpace(NewSceneName));
        RunCloudCommand = new AsyncCommand(async p =>
        {
            var s = (CloudScene)p!;
            await _main.Manager.Cloud!.TriggerSceneAsync(s.HomeId, s.SceneId);
            Status.Info($"Cloud scene '{s.Name}' triggered");
        });
        ToggleAutomationCommand = new AsyncCommand(async p =>
        {
            var a = (CloudAutomation)p!;
            await _main.Manager.Cloud!.SetAutomationEnabledAsync(a.HomeId, a.AutomationId, !a.Enabled);
            await _main.Manager.RefreshCloudScenesAsync();
            Status.Info($"Automation '{a.Name}' {(a.Enabled ? "disabled" : "enabled")}");
        });
        RefreshCloudCommand = new AsyncCommand(() => _main.Manager.RefreshCloudScenesAsync(), _ => _main.Manager.Cloud != null);
        Reload();
    }

    public ObservableCollection<LocalScene> LocalScenes { get; } = [];
    public ObservableCollection<CloudScene> CloudScenes { get; } = [];
    public ObservableCollection<CloudAutomation> CloudAutomations { get; } = [];
    public IEnumerable<DeviceViewModel> Devices => _main.Devices;

    public ICommand RunLocalCommand { get; }
    public ICommand DeleteLocalCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand RunCloudCommand { get; }
    public ICommand ToggleAutomationCommand { get; }
    public ICommand RefreshCloudCommand { get; }

    public string NewSceneName
    {
        get => _newSceneName;
        set => Set(ref _newSceneName, value);
    }

    public void Reload()
    {
        LocalScenes.Clear();
        foreach (var s in _main.Manager.Config.Scenes) LocalScenes.Add(s);
        CloudScenes.Clear();
        foreach (var s in _main.Manager.CloudScenes) CloudScenes.Add(s);
        CloudAutomations.Clear();
        foreach (var a in _main.Manager.CloudAutomations) CloudAutomations.Add(a);
        OnPropertyChanged(nameof(Devices));
    }

    private void Capture()
    {
        var ids = _main.Devices.Where(d => d.IsSelected).Select(d => d.Id).ToList();
        var scene = _main.Manager.CaptureScene(NewSceneName.Trim(), ids);
        LocalScenes.Add(scene);
        Status.Info($"Scene '{scene.Name}' saved with {scene.Actions.Count} device(s)");
    }
}

// ==================================================================== schedules

public sealed record TargetOption(ScheduleTarget Type, string Id, string Name, string? HomeId = null)
{
    public string Display => $"{Type switch { ScheduleTarget.Group => "Group", ScheduleTarget.LocalScene => "Scene", ScheduleTarget.CloudScene => "Cloud scene", _ => "Device" }}: {Name}";
}

public sealed class DayOption(DayOfWeek day, bool selected) : ObservableObject
{
    private bool _selected = selected;
    public DayOfWeek Day { get; } = day;
    public string Name => CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[(int)Day];

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}

public sealed class SchedulesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _time = "07:00";
    private string _name = "";
    private TargetOption? _target;
    private ScheduleAction _action = ScheduleAction.TurnOn;
    private string _brightness = "";

    public SchedulesViewModel(MainViewModel main)
    {
        _main = main;
        AddCommand = new RelayCommand(Add, _ => Target != null && TimeOnly.TryParse(Time, CultureInfo.InvariantCulture, out TimeOnly _));
        DeleteCommand = new RelayCommand(p =>
        {
            var e = (ScheduleEntry)p!;
            _main.Manager.Config.Schedules.Remove(e);
            _main.Manager.Save();
            Entries.Remove(e);
        });
        RunNowCommand = new AsyncCommand(p => _main.Scheduler.RunAsync((ScheduleEntry)p!));
        SaveCommand = new RelayCommand(() => _main.Manager.Save());
        Days = [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new DayOption(d, true))];
        Reload();
    }

    public ObservableCollection<ScheduleEntry> Entries { get; } = [];
    public ObservableCollection<TargetOption> Targets { get; } = [];
    public IReadOnlyList<DayOption> Days { get; }
    public IReadOnlyList<ScheduleAction> Actions { get; } = Enum.GetValues<ScheduleAction>();
    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RunNowCommand { get; }
    public ICommand SaveCommand { get; }

    public string Time { get => _time; set => Set(ref _time, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public TargetOption? Target { get => _target; set => Set(ref _target, value); }
    public ScheduleAction Action { get => _action; set => Set(ref _action, value); }
    public string Brightness { get => _brightness; set => Set(ref _brightness, value); }

    public void Reload()
    {
        Entries.Clear();
        foreach (var e in _main.Manager.Config.Schedules) Entries.Add(e);
        Targets.Clear();
        foreach (var d in _main.Devices) Targets.Add(new TargetOption(ScheduleTarget.Device, d.Id, d.Name));
        foreach (var g in _main.Manager.Config.Groups) Targets.Add(new TargetOption(ScheduleTarget.Group, g.Id, g.Name));
        foreach (var s in _main.Manager.Config.Scenes) Targets.Add(new TargetOption(ScheduleTarget.LocalScene, s.Id, s.Name));
        foreach (var s in _main.Manager.CloudScenes) Targets.Add(new TargetOption(ScheduleTarget.CloudScene, s.SceneId, s.Name, s.HomeId));
    }

    private void Add()
    {
        var t = Target!;
        var e = new ScheduleEntry
        {
            Name = string.IsNullOrWhiteSpace(Name) ? $"{t.Name} {Action}" : Name.Trim(),
            Time = TimeOnly.Parse(Time, CultureInfo.InvariantCulture),
            Days = Days.Where(d => d.Selected).Select(d => d.Day).ToList(),
            TargetType = t.Type,
            TargetId = t.Id,
            HomeId = t.HomeId,
            Action = t.Type is ScheduleTarget.LocalScene or ScheduleTarget.CloudScene ? ScheduleAction.RunScene : Action,
            Brightness = int.TryParse(Brightness, out var b) ? Math.Clamp(b, 1, 100) : null,
        };
        _main.Manager.Config.Schedules.Add(e);
        _main.Manager.Save();
        Entries.Add(e);
        Status.Info($"Schedule '{e.Name}' added");
    }
}


