// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System;
using System.Net.Sockets;

namespace MouseWithoutBorders.Class;

internal static class TcpConnectionRetry
{
    // The old desktop may release its socket just before the new listener starts.
    // Keep that short refusal window independent of the owner's startup deadline.
    private const int FastRetryMilliseconds = 50;
    private const int FastRetryWindowMilliseconds = 1000;
    private const int MaximumFastRetries = FastRetryWindowMilliseconds / FastRetryMilliseconds;

    internal static bool TryConnect(
        Action connect,
        long originalDeadline,
        Func<bool> isClosed,
        Func<long> getLegacyTick,
        Func<long> getMonotonicTick,
        Action<int> delay,
        out SocketException failure)
    {
        failure = null;
        long? firstHandoffFailure = null;
        int fastRetries = 0;

        while (!isClosed())
        {
            try
            {
                connect();
                // Close can race with a successful blocking Connect. Never enter
                // the protocol on behalf of an owner that already released it.
                if (isClosed())
                {
                    return false;
                }

                failure = null;
                return true;
            }
            catch (SocketException error)
            {
                if (isClosed())
                {
                    return false;
                }

                failure = error;
                bool handoffFailure = error.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset;
                if (handoffFailure && !firstHandoffFailure.HasValue)
                {
                    // A native Connect can take seconds before reporting refusal.
                    // That first call must not consume the listener-start grace.
                    firstHandoffFailure = getMonotonicTick();
                }

                long fastTimeLeft = firstHandoffFailure.HasValue
                    ? FastRetryWindowMilliseconds - (getMonotonicTick() - firstHandoffFailure.Value)
                    : 0;
                int wait;
                if (handoffFailure && fastRetries < MaximumFastRetries && fastTimeLeft > 0)
                {
                    wait = (int)Math.Min(FastRetryMilliseconds, fastTimeLeft);
                    fastRetries++;
                }
                else
                {
                    long timeLeft = originalDeadline - getLegacyTick();
                    if (timeLeft <= 0)
                    {
                        return false;
                    }

                    // Keep the existing slow retry budget for ordinary network
                    // failures and after the bounded desktop-handoff window.
                    wait = (int)Math.Min(1000, timeLeft);
                }

                delay(wait);
                // Check at the top of the loop before another Connect. In
                // particular, disposal during the delay must not revive a socket.
            }
        }

        return false;
    }
}
