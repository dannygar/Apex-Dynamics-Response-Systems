using Microsoft.EntityFrameworkCore;

namespace Evaluation.Core;

public static class JobLease
{
    public const int Seconds = 120;
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(Seconds);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    public static async Task<bool> TryClaim(EvaluationDb db, Guid id, string owner, long now, CancellationToken ct = default) =>
        await db.Outbox.Where(x => x.Id == id && (x.State == "Pending" || x.State == "Leased") &&
            x.LeaseUntil < now && x.AvailableAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseOwner, owner).SetProperty(x => x.LeaseUntil, now + Seconds)
                .SetProperty(x => x.State, "Leased").SetProperty(x => x.Attempts, x => x.Attempts + 1), ct) == 1;

    public static async Task<bool> Extend(EvaluationDb db, Guid id, string owner, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return await db.Outbox.Where(x => x.Id == id && x.LeaseOwner == owner && x.State == "Leased" && x.LeaseUntil > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, now + Seconds), ct) == 1;
    }
    public static async Task<bool> Finish(EvaluationDb db, Guid id, string owner, string state, CancellationToken ct = default, long available = 0)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return await db.Outbox.Where(x => x.Id == id && x.LeaseOwner == owner && x.State == "Leased" && x.LeaseUntil > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state).SetProperty(x => x.LeaseUntil, 0)
                .SetProperty(x => x.LeaseOwner, (string?)null).SetProperty(x => x.AvailableAt, available), ct) == 1;
    }
}

// Keeps a job lease alive while a long external call is in flight. If the lease is lost (another owner,
// or no successful renewal before it would expire), Token is cancelled so the caller stops without writing.
public sealed class LeaseKeeper : IAsyncDisposable
{
    private readonly CancellationTokenSource linked;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    public bool Lost { get; private set; }
    public CancellationToken Token => linked.Token;

    private LeaseKeeper(Func<CancellationToken, Task<bool>> extend, TimeSpan interval, TimeSpan duration, CancellationToken parent)
    {
        linked = CancellationTokenSource.CreateLinkedTokenSource(parent);
        loop = Run(extend, interval, duration);
    }
    public static LeaseKeeper Start(Func<CancellationToken, Task<bool>> extend, TimeSpan interval, TimeSpan duration, CancellationToken parent) =>
        new(extend, interval, duration, parent);

    private async Task Run(Func<CancellationToken, Task<bool>> extend, TimeSpan interval, TimeSpan duration)
    {
        var lastRenewal = DateTimeOffset.UtcNow;
        while (!stop.IsCancellationRequested && !linked.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stop.Token); }
            catch (OperationCanceledException) { return; }
            bool? renewed;
            try { renewed = await extend(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception) { renewed = null; } // Transient storage failure: retry until the lease would expire.
            if (renewed == true) { lastRenewal = DateTimeOffset.UtcNow; continue; }
            if (renewed == false || DateTimeOffset.UtcNow - lastRenewal >= duration - interval)
            {
                Lost = true;
                try { linked.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
        stop.Dispose();
        linked.Dispose();
    }
}
