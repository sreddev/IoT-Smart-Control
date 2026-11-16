using System.Collections.ObjectModel;
using System.Windows.Input;
using LedvanceControl.App.Mvvm;
using LedvanceControl.Core.Model;
using LedvanceControl.Core.Services;

namespace LedvanceControl.App.ViewModels;

/// <summary>One house in the sidebar picker (list mode) and in its "Manage houses" panel.</summary>
public sealed class HouseOption : ObservableObject
{
    private string _name = "";
    private string _editName = "";
    private bool _isCurrent;
    private bool _canDelete;
    private string _deleteTip = "";
    private string _summary = "";

    public HouseOption(string id) => Id = id;

    public string Id { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>Name box in the Manage panel (committed with Save / Enter).</summary>
    public string EditName { get => _editName; set => Set(ref _editName, value); }
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public bool CanDelete { get => _canDelete; set => Set(ref _canDelete, value); }
    public string DeleteTip { get => _deleteTip; set => Set(ref _deleteTip, value); }
    /// <summary>"2 rooms · 3 devices · SmartThings".</summary>
    public string Summary { get => _summary; set => Set(ref _summary, value); }
}

/// <summary>
/// The house picker at the top of the sidebar: switches houses and, through "Manage houses…", adds,
/// renames and deletes them. The option list is updated in place (never cleared), so the picker keeps
/// showing the selected house while houses are added or renamed.
/// </summary>
public sealed class HousePickerViewModel : ObservableObject
{
    private readonly DeviceManager _manager;
    private bool _isOpen;
    private bool _isManaging;
    private string _newHouseName = "";

    public HousePickerViewModel(DeviceManager manager)
    {
        _manager = manager;
        PickCommand = new RelayCommand(p => Pick((HouseOption)p!));
        ManageCommand = new RelayCommand(() => IsManaging = true);
        DoneCommand = new RelayCommand(() => { IsOpen = false; IsManaging = false; });
        AddCommand = new RelayCommand(Add, _ => !string.IsNullOrWhiteSpace(NewHouseName));
        SaveCommand = new RelayCommand(p => Rename((HouseOption)p!), p => p is HouseOption o && !string.IsNullOrWhiteSpace(o.EditName) && o.EditName.Trim() != o.Name);
        DeleteCommand = new RelayCommand(p => Delete((HouseOption)p!), p => p is HouseOption { CanDelete: true });
        Reload();
    }

    public ObservableCollection<HouseOption> Options { get; } = [];
    public string CurrentName => _manager.CurrentHouse.Name;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!Set(ref _isOpen, value)) return;
            if (!value) IsManaging = false; // reopening starts at the list again
            else Reload();
        }
    }

    public bool IsManaging { get => _isManaging; set => Set(ref _isManaging, value); }
    public string NewHouseName { get => _newHouseName; set => Set(ref _newHouseName, value); }

    public ICommand PickCommand { get; }
    public ICommand ManageCommand { get; }
    public ICommand DoneCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>Brings the options in line with the houses without clearing the list.</summary>
    public void Reload()
    {
        var houses = _manager.Houses;
        for (var i = Options.Count - 1; i >= 0; i--)
            if (houses.All(h => h.Id != Options[i].Id)) Options.RemoveAt(i);
        for (var i = 0; i < houses.Count; i++)
        {
            var h = houses[i];
            var opt = Options.FirstOrDefault(o => o.Id == h.Id);
            if (opt == null) Options.Insert(Math.Min(i, Options.Count), opt = new HouseOption(h.Id) { EditName = h.Name });
            else if (Options.IndexOf(opt) != i) Options.Move(Options.IndexOf(opt), i);
            if (opt.Name != h.Name) { opt.Name = h.Name; opt.EditName = h.Name; }
            Describe(opt, h);
        }
        OnPropertyChanged(nameof(CurrentName));
    }

    private void Describe(HouseOption opt, HouseConfig h)
    {
        var devices = _manager.Config.Devices.Count(d => d.HouseId == h.Id);
        var rooms = _manager.RoomsOf(h.Id).Count;
        var source = h.SmartThingsLocationId != null ? " · SmartThings" : h.TuyaHomeId != null ? " · Tuya" : "";
        opt.IsCurrent = h.Id == _manager.CurrentHouse.Id;
        opt.Summary = $"{rooms} room{(rooms == 1 ? "" : "s")} · {devices} device{(devices == 1 ? "" : "s")}{source}";
        opt.CanDelete = _manager.Houses.Count > 1 && devices == 0;
        opt.DeleteTip = _manager.Houses.Count == 1 ? "Your only house can't be deleted — rename it instead"
            : devices > 0 ? $"Has {devices} device(s): remove or move them first" : "Delete this house";
    }

    private void Pick(HouseOption opt)
    {
        IsOpen = false;
        _manager.SetCurrentHouse(opt.Id); // ListChanged re-scopes the pages
    }

    private void Add()
    {
        try
        {
            var house = _manager.AddHouse(NewHouseName); // stays on the current house
            NewHouseName = "";
            Reload();
            Status.Info($"Created '{house.Name}'. Still showing {CurrentName} — pick '{house.Name}' from the house list to open it.");
        }
        catch (Exception ex) { Status.Error(ex); }
    }

    private void Rename(HouseOption opt)
    {
        try
        {
            _manager.RenameHouse(opt.Id, opt.EditName);
            Reload();
            Status.Info($"Renamed house to '{opt.Name}'.");
        }
        catch (Exception ex) { Status.Error(ex); opt.EditName = opt.Name; }
    }

    private void Delete(HouseOption opt)
    {
        try
        {
            var name = opt.Name;
            _manager.DeleteHouse(opt.Id);
            Reload();
            Status.Info($"Deleted house '{name}'.");
        }
        catch (Exception ex) { Status.Error(ex); }
    }
}
