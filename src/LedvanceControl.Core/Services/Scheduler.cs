using LedvanceControl.Core.Model;

namespace LedvanceControl.Core.Services;

/// <summary>
/// Executes <see cref="ScheduleEntry"/> timers while the application is running.
/// Checks every 15 seconds; an entry fires once per day when its time has passed.
/// </summary>
public sealed class Scheduler : IDisposable
{
    private readonly DeviceManager _manager;
    private readonly Timer _timer;
    private DateTime _lastCheck = DateTime.Now;

    public Scheduler(DeviceManager manager)
    {
        _manager = manager;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    /// <summary>Raised after a schedule ran (entry, list of errors).</summary>
    public event Action<ScheduleEntry, IReadOnlyList<string>>? Executed;

    private void Tick()
    {
        var now = DateTime.Now;
        var from = _lastCheck;
        _lastCheck = now;
        foreach (var e in _manager.Config.Schedules.ToList())
        {
            if (!e.Enabled || !IsDue(e, from, now)) continue;
            e.LastRun = now;
            _ = RunAsync(e);
        }
    }

    /// <summary>True when the entry's time of day falls within (from, to] on one of its days.</summary>
    public static bool IsDue(ScheduleEntry e, DateTime from, DateTime to)
    {
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var at = day + e.Time.ToTimeSpan();
            if (at > from && at <= to && e.Days.Contains(day.DayOfWeek) && e.LastRun?.Date != day) return true;
        }
        return false;
    }

    public async Task RunAsync(ScheduleEntry e)
    {
        IReadOnlyList<string> errors;
        try
        {
            errors = await ExecuteAsync(e);
        }
        catch (Exception ex)
        {
            errors = [ex.Message];
        }
        _manager.Save();
        Executed?.Invoke(e, errors);
    }

    private async Task<IReadOnlyList<string>> ExecuteAsync(ScheduleEntry e)
    {
        switch (e.TargetType)
        {
            case ScheduleTarget.LocalScene:
                var scene = _manager.Config.Scenes.FirstOrDefault(s => s.Id == e.TargetId);
                return scene == null ? ["Scene no longer exists"] : await _manager.RunSceneAsync(scene);
            case ScheduleTarget.CloudScene:
                if (_manager.Cloud == null) return ["Cloud is not configured"];
                await _manager.Cloud.TriggerSceneAsync(e.HomeId!, e.TargetId);
                return [];
        }

        IEnumerable<DeviceRuntime> targets = e.TargetType == ScheduleTarget.Group
            ? _manager.Config.Groups.FirstOrDefault(g => g.Id == e.TargetId) is { } g ? _manager.Members(g) : []
            : _manager.Find(e.TargetId) is { } d ? [d] : [];

        return await _manager.ForEachAsync(targets, async dev =>
        {
            bool on = e.Action switch
            {
                ScheduleAction.TurnOff => false,
                ScheduleAction.Toggle => dev.IsOn != true,
                _ => true,
            };
            if (on && e.Brightness is { } b && dev.Light?.HasBrightness == true)
            {
                var values = new Dictionary<string, object?> { [dev.Light.SwitchCode!] = true, [dev.Light.Bright!.Code] = dev.Light.BrightnessToRaw(b) };
                await dev.SetAsync(values);
            }
            else
            {
                await dev.SetPowerAsync(on);
            }
        });
    }

    public void Dispose() => _timer.Dispose();
}
