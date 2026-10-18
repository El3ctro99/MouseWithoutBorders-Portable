// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ServiceProcess;

namespace MouseWithoutBorders.SecureDesktop;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--validate-installation")
            {
                // Read-only installer preflight. This never starts a service,
                // launches a process, modifies permissions, or requests UAC.
                SecureDesktopWorker.ValidateProfile();
                return 0;
            }

            if (args.Length != 0) return 87;
            ServiceTrust.RequireLocalSystem();
            if (!string.Equals(Path.GetFullPath(Environment.ProcessPath ?? string.Empty), ServiceTrust.ServicePath, StringComparison.OrdinalIgnoreCase))
            {
                return 5;
            }

            ServiceBase.Run(new SecureDesktopWorker());
            return 0;
        }
        catch
        {
            // Never echo a profile, security key or user-supplied command line.
            return 1;
        }
    }
}
