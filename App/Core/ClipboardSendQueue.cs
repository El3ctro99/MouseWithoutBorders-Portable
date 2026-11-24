// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MouseWithoutBorders.Core;

/// <summary>Orders complete clipboard sends without holding an input or socket lock.</summary>
internal sealed class ClipboardSendQueue
{
    private readonly object sync = new();
    private Task tail = Task.CompletedTask;

    internal Task Enqueue(Action send)
    {
        ArgumentNullException.ThrowIfNull(send);

        lock (sync)
        {
            Task queued = tail.ContinueWith(previous =>
            {
                // A failed send must not cancel the rest of the queue.
                _ = previous.Exception;
                send();
            }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            tail = queued;

            // Callers may deliberately fire and forget. Keep the returned task faulted,
            // but observe every failure even when no later send is enqueued.
            _ = queued.ContinueWith(completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return queued;
        }
    }
}
