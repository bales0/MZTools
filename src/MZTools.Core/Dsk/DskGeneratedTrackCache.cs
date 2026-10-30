using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MZTools;

// Snapshot-scoped, bounded cache. Only requested tracks are encoded, one at a time.
internal sealed class DskGeneratedTrackCache : IDisposable
{
    private readonly DskVisualSnapshot snapshot;
    private readonly Dictionary<int,PhysicalTrack> tracks=new();
    private readonly LinkedList<int> order=new();
    private readonly SemaphoreSlim gate=new(1);
    private readonly CancellationTokenSource lifetime=new();
    internal int Count=>tracks.Count;
    internal DskGeneratedTrackCache(DskVisualSnapshot snapshot) { this.snapshot=snapshot; }
    internal async Task<PhysicalTrack> GetAsync(int track,CancellationToken cancellation=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);
        await gate.WaitAsync(linked.Token);
        try {
            linked.Token.ThrowIfCancellationRequested();
            if(tracks.TryGetValue(track,out var cached)) { order.Remove(track); order.AddLast(track); return cached; }
            var result=await Task.Run(()=>snapshot.Generate(track,linked.Token),linked.Token);
            linked.Token.ThrowIfCancellationRequested(); tracks[track]=result; order.AddLast(track);
            while(order.Count>8) { tracks.Remove(order.First!.Value); order.RemoveFirst(); }
            return result;
        } finally { gate.Release(); }
    }
    public void Dispose() { lifetime.Cancel(); }
}
