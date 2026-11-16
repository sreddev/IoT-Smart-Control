using System.Diagnostics;
using LedvanceControl.Core.Services;
using Xunit;

namespace LedvanceControl.Tests;

public class SenderTests
{
    [Fact]
    public async Task FirstChange_IsSentImmediately()
    {
        // a long interval: if the first change waited for it (like the old debouncer), this would take 10 s
        var sender = new LatestOnlySender(TimeSpan.FromSeconds(10));
        var sent = new TaskCompletionSource<long>();
        var sw = Stopwatch.StartNew();
        sender.Run(() => { sent.TrySetResult(sw.ElapsedMilliseconds); return Task.CompletedTask; });
        var ms = await sent.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(ms < 3000, $"first change waited {ms} ms");
    }

    [Fact]
    public async Task Drag_SendsFirstAndLast_SkipsOnesInBetween()
    {
        var sender = new LatestOnlySender(TimeSpan.FromMilliseconds(100));
        var sent = new List<int>();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        // value 0 is "in flight" until the gate opens; 1..9 arrive meanwhile
        sender.Run(async () => { lock (sent) sent.Add(0); started.SetResult(); await gate.Task; });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 1; i <= 9; i++)
        {
            var v = i;
            sender.Run(() => { lock (sent) sent.Add(v); return Task.CompletedTask; });
        }
        gate.SetResult();
        await sender.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([0, 9], sent);
    }

    [Fact]
    public async Task Error_IsReported_AndLaterChangesStillSend()
    {
        var errors = new List<Exception>();
        var sender = new LatestOnlySender(TimeSpan.Zero, errors.Add);
        sender.Run(() => throw new InvalidOperationException("bulb offline"));
        await sender.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var ok = false;
        sender.Run(() => { ok = true; return Task.CompletedTask; });
        await sender.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(errors);
        Assert.True(ok);
    }
}
