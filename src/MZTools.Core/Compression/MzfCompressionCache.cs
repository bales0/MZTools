using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

namespace MZTools;

// Only the pure compressed stream is shared. Loader/header construction and target
// validation still run for every request, using that request's current metadata.
internal sealed class MzfCompressionCache(long maximumBytes = 32 * 1024 * 1024)
{
    private readonly record struct Key(string Hash, int Length, MzfCompressionOptions Options);
    private sealed record Entry(Key Key, CompressionPayload Payload, long Cost);
    private readonly object gate = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> entries = new();
    private readonly LinkedList<Entry> order = new();
    private long bytes;
    internal long Bytes { get { lock(gate)return bytes; } }
    internal int Count { get { lock(gate)return entries.Count; } }
    private static CompressionPayload Copy(CompressionPayload payload) => new((byte[])payload.Data.Clone(), payload.Delta);

    internal CompressionPayload GetOrCreate(byte[] input, MzfCompressionOptions options,
        Func<byte[], CompressionPayload> compute, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        byte[] snapshot=(byte[])input.Clone();
        var key=new Key(Convert.ToHexString(SHA256.HashData(snapshot)),snapshot.Length,options);
        lock(gate)
        {
            if(entries.TryGetValue(key,out var node))
            {
                cancellation.ThrowIfCancellationRequested();order.Remove(node);order.AddFirst(node);return Copy(node.Value.Payload);
            }
        }
        var result=compute(snapshot);
        cancellation.ThrowIfCancellationRequested();
        // Bound both data and entry overhead, including tiny inputs.
        long cost=result.Data.LongLength+256;
        if(cost<=maximumBytes)
        {
            var stored=Copy(result);
            lock(gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if(!entries.ContainsKey(key))
                {
                    while(bytes+cost>maximumBytes&&order.Last is{} last)
                    {order.RemoveLast();entries.Remove(last.Value.Key);bytes-=last.Value.Cost;}
                    var node=order.AddFirst(new Entry(key,stored,cost));entries.Add(key,node);bytes+=cost;
                }
            }
        }
        return result;
    }
}

internal static class MzfCompressionSizes
{
    internal static string Describe(int packed,int original)
    {
        string ratio=original==0?"n/a":$"{100.0*packed/original:F1}% of original; saving {100.0*(original-packed)/original:F1}%";
        return $"Program data (including loader): {packed:N0} / {original:N0} B ({ratio})";
    }
    internal static string Describe(TapeRecord record) =>
        MzfLoaderBuilder.TryGetCompressionInfo(record,out var info)&&info is not null
        ? Describe(record.Body.MzfBody.Length,info.RestoredSize) : $"Program data: {record.Body.MzfBody.Length:N0} B (uncompressed size not inferred)";
}
