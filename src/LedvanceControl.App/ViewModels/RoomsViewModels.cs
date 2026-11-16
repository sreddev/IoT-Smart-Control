using System.Collections.ObjectModel;
using System.Windows.Input;
using LedvanceControl.App.Mvvm;
using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Services;

namespace LedvanceControl.App.ViewModels;

/// <summary>
/// One room of a house: its devices (a device is in exactly one room of its house, so ticking it here
/// moves it from wherever it was) plus whole-room control, like a group.
/// </summary>
public sealed class RoomViewModel : ObservableObject, ILightTarget
{
    private readonly DeviceManager _manager;
    private string _editName;

    public RoomViewModel(DeviceManager manager, string houseId, string name, IEnumerable<DeviceViewModel> houseDevices)
    {
        _manager = manager;
        HouseId = houseId;
        Name = name;
        _editName = name;
        Light = new LightControlViewModel(this);
        Members = houseDevices
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new RoomMember(d, IsIn(d), this))
            .ToList();
        OnCommand = new AsyncCommand(async () => Status.Errors($"{Name} on", await _manager.SetRoomPowerAsync(HouseId, Name, true)));
        OffCommand = new AsyncCommand(async () => Status.Errors($"{Name} off", await _manager.SetRoomPowerAsync(HouseId, Name, false)));
    }

    public string HouseId { get; }
    public string Name { get; }
    public LightControlViewModel Light { get; }
    public IReadOnlyList<RoomMember> Members { get; }
    public ICommand OnCommand { get; }
    public ICommand OffCommand { get; }

    public int DeviceCount => _manager.DevicesInRoom(HouseId, Name).Count();
    public string Summary => DeviceCount switch { 0 => "Empty", 1 => "1 device", var n => $"{n} devices" };

    /// <summary>Name box; committed on focus loss (renames the room in this house).</summary>
    public string EditName
    {
        get => _editName;
        set
        {
            if (!Set(ref _editName, value)) return;
            if (string.IsNullOrWhiteSpace(value) || value.Trim() == Name) return;
            try
            {
                RoomsViewModel.SelectAfterReload = value.Trim();
                _manager.RenameRoom(HouseId, Name, value);
                Status.Info($"Renamed room '{Name}' to '{value.Trim()}'");
            }
            catch (Exception ex) { Status.Error(ex); _editName = Name; OnPropertyChanged(); }
        }
    }

    private bool IsIn(DeviceViewModel d) =>
        string.Equals(d.Runtime.Config.Room?.Trim(), Name, StringComparison.CurrentCultureIgnoreCase);

    internal void SetMembership(DeviceViewModel d, bool member)
    {
        try { _manager.SetDeviceRoom(d.Id, member ? Name : null); }
        catch (Exception ex) { Status.Error(ex); }
    }

    private IEnumerable<LightProfile> Profiles => _manager.DevicesInRoom(HouseId, Name).Select(d => d.Light).OfType<LightProfile>();
    bool ILightTarget.HasTemp => Profiles.Any(p => p.HasTemp);
    bool ILightTarget.HasColour => Profiles.Any(p => p.HasColour);
    bool ILightTarget.HasScenes => Profiles.Any(p => p.HasScenes);

    async Task ILightTarget.SetPowerAsync(bool on) => Status.Errors($"{Name} power", await _manager.SetRoomPowerAsync(HouseId, Name, on));

    async Task ILightTarget.ApplyAsync(Func<LightProfile, IReadOnlyDictionary<string, object?>> build)
    {
        var errors = await _manager.SetRoomLightAsync(HouseId, Name, build);
        if (errors.Count > 0) Status.Errors(Name, errors);
    }

    public sealed class RoomMember(DeviceViewModel device, bool isMember, RoomViewModel room) : ObservableObject
    {
        private bool _isMember = isMember;
        public DeviceViewModel Device { get; } = device;

        /// <summary>The room the device is in now, when it is not this one (shown next to the name).</summary>
        public string? OtherRoom => !_isMember && !string.IsNullOrWhiteSpace(Device.Runtime.Config.Room) ? Device.Runtime.Config.Room : null;
        /// <summary>"  (Living room)" or empty — preformatted because a StringFormat would still print "()" for null.</summary>
        public string OtherRoomSuffix => OtherRoom is { } r ? $"  ({r})" : "";

        public bool IsMember
        {
            get => _isMember;
            set { if (Set(ref _isMember, value)) room.SetMembership(Device, value); }
        }
    }
}

/// <summary>The Rooms page: the rooms of the selected house (houses are managed in the sidebar picker).</summary>
public sealed class RoomsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private RoomViewModel? _selected;

    public RoomsViewModel(MainViewModel main)
    {
        _main = main;
        AddCommand = new RelayCommand(Add);
        DeleteCommand = new RelayCommand(Delete, _ => Selected != null);
        Reload();
    }

    private DeviceManager Manager => _main.Manager;

    public ObservableCollection<RoomViewModel> Rooms { get; } = [];
    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }

    public RoomViewModel? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>Name of the selected house (read-only here).</summary>
    public string HouseName => Manager.CurrentHouse.Name;

    public string UnassignedSummary
    {
        get
        {
            var n = Manager.Config.Devices.Count(d => d.HouseId == Manager.CurrentHouse.Id && string.IsNullOrWhiteSpace(d.Room));
            return n == 0 ? "Every device in this house is in a room." : $"{n} device(s) here are not in any room — tick them in a room to assign them.";
        }
    }

    /// <summary>Room to select after the next reload (ListChanged reaches the UI asynchronously).</summary>
    internal static string? SelectAfterReload;

    public void Reload()
    {
        var name = SelectAfterReload ?? Selected?.Name;
        SelectAfterReload = null;
        var house = Manager.CurrentHouse;
        var houseDevices = _main.Devices.Where(d => d.HouseId == house.Id).ToList();
        Rooms.Clear();
        foreach (var r in Manager.RoomsOf(house.Id)) Rooms.Add(new RoomViewModel(Manager, house.Id, r, houseDevices));
        Selected = Rooms.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.CurrentCultureIgnoreCase))
                   ?? Rooms.FirstOrDefault();
        OnPropertyChanged(nameof(HouseName));
        OnPropertyChanged(nameof(UnassignedSummary));
    }

    private void Add()
    {
        var house = Manager.CurrentHouse;
        var existing = Manager.RoomsOf(house.Id);
        var n = 1;
        string name;
        do name = n == 1 ? "New room" : $"New room {n}"; while (existing.Contains(name, StringComparer.CurrentCultureIgnoreCase) && n++ < 1000);
        try
        {
            SelectAfterReload = name;
            Manager.AddRoom(house.Id, name); // ListChanged → Reload
            Status.Info($"Added '{name}' to {house.Name}. Rename it and tick its devices.");
        }
        catch (Exception ex) { Status.Error(ex); }
    }

    private void Delete()
    {
        if (Selected == null) return;
        var (house, name) = (Manager.CurrentHouse, Selected.Name);
        Manager.DeleteRoom(house.Id, name);
        Status.Info($"Deleted room '{name}' from {house.Name}; its devices now have no room");
    }


}
