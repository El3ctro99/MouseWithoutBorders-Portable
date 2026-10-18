// Copyright (c) Microsoft Corporation
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.PowerToys.Settings.UI.Library;

namespace MouseWithoutBorders.Core;

/// <summary>An explicit, bounded input-only snapshot reviewed by an administrator.</summary>
internal sealed class SecureDesktopProfile
{
    internal const int MaximumBytes = 64 * 1024;
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "securityKey", "localMachineName", "machineId", "machineMatrix",
        "matrixOneRow", "tcpPort", "ipMappings", "wrapMouse", "easyMouse",
        "moveMouseRelatively", "validateRemoteMachineIP", "sameSubnetOnly",
    };

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("securityKey")] public string SecurityKey { get; set; }
    [JsonPropertyName("localMachineName")] public string LocalMachineName { get; set; }
    [JsonPropertyName("machineId")] public int MachineId { get; set; }
    [JsonPropertyName("machineMatrix")] public string[] MachineMatrix { get; set; }
    [JsonPropertyName("matrixOneRow")] public bool MatrixOneRow { get; set; }
    [JsonPropertyName("tcpPort")] public int TcpPort { get; set; }
    [JsonPropertyName("ipMappings")] public string IpMappings { get; set; }
    [JsonPropertyName("wrapMouse")] public bool WrapMouse { get; set; }
    [JsonPropertyName("easyMouse")] public int EasyMouse { get; set; }
    [JsonPropertyName("moveMouseRelatively")] public bool MoveMouseRelatively { get; set; }
    [JsonPropertyName("validateRemoteMachineIP")] public bool ValidateRemoteMachineIP { get; set; }
    [JsonPropertyName("sameSubnetOnly")] public bool SameSubnetOnly { get; set; }

    internal static SecureDesktopProfile Parse(string json, string expectedMachineName)
    {
        if (json == null || System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("The secure desktop profile exceeds its size limit.");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The secure desktop profile must be an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in document.RootElement.EnumerateObject())
            {
                if (!Fields.Contains(field.Name) || !seen.Add(field.Name))
                    throw new InvalidDataException("The secure desktop profile contains unsupported or duplicate fields.");
            }
            if (!seen.SetEquals(Fields)) throw new InvalidDataException("The secure desktop profile is incomplete.");
            var profile = JsonSerializer.Deserialize<SecureDesktopProfile>(json)
                ?? throw new InvalidDataException("The secure desktop profile is empty.");
            profile.Validate(expectedMachineName);
            return profile;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The secure desktop profile is not valid JSON.", error);
        }
    }

    internal void Validate(string expectedMachineName)
    {
        if (SchemaVersion != 1 || SecurityKey == null || SecurityKey.Length is < 4 or > 1024
            || SecurityKey.Any(char.IsControl) || MachineId <= 0 || TcpPort is < 1 or > 65534
            || EasyMouse is < 0 or > 3 || !ValidMachineName(LocalMachineName)
            || !string.Equals(LocalMachineName, expectedMachineName, StringComparison.OrdinalIgnoreCase)
            || MachineMatrix == null || MachineMatrix.Length != 4 || IpMappings == null || IpMappings.Length > 8192)
            throw new InvalidDataException("The secure desktop profile contains invalid input or identity settings.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in MachineMatrix)
        {
            if (name == null || (name.Length > 0 && (!ValidMachineName(name) || !names.Add(name))))
                throw new InvalidDataException("The secure desktop machine layout is invalid.");
        }
        if (!names.Contains(LocalMachineName))
            throw new InvalidDataException("The secure desktop layout must include this computer.");
        if (IpMappings.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new InvalidDataException("The secure desktop IP mappings are invalid.");
    }

    private static bool ValidMachineName(string value) => !string.IsNullOrEmpty(value)
        && value.Length <= 63 && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "\\/:*?\"<>|,;".Contains(c));

    internal static SecureDesktopProfile FromPreferences(MouseWithoutBordersSettings settings, string machineName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var p = settings.Properties ?? throw new InvalidDataException("Configure Mouse Without Borders before exporting a profile.");
        var profile = new SecureDesktopProfile
        {
            SecurityKey = p.SecurityKey.Value,
            LocalMachineName = machineName,
            MachineId = p.MachineID.Value,
            MachineMatrix = p.MachineMatrixString.ToArray(),
            MatrixOneRow = p.MatrixOneRow,
            TcpPort = p.TCPPort.Value,
            IpMappings = p.Name2IP.Value,
            WrapMouse = p.WrapMouse,
            EasyMouse = p.EasyMouse.Value,
            MoveMouseRelatively = p.MoveMouseRelatively,
            ValidateRemoteMachineIP = p.ValidateRemoteMachineIP,
            SameSubnetOnly = p.SameSubnetOnly,
        };
        profile.Validate(machineName);
        return profile;
    }

    internal MouseWithoutBordersSettings CreateInputOnlySettings()
    {
        var settings = new MouseWithoutBordersSettings();
        var p = settings.Properties;
        p.SecurityKey.Value = SecurityKey;
        p.MachineID.Value = MachineId;
        p.MachineMatrixString = MachineMatrix.ToList();
        // Seed the reviewed names before the first authenticated heartbeat.
        // Empty discovery state would enter the ordinary app's automatic setup.
        p.MachinePool.Value = string.Join(",", MachineMatrix.Select(name => name + ":" +
            (string.Equals(name, LocalMachineName, StringComparison.OrdinalIgnoreCase) ? MachineId : 0)
                .ToString(CultureInfo.InvariantCulture)));
        p.MatrixOneRow = MatrixOneRow;
        p.TCPPort.Value = TcpPort;
        p.Name2IP.Value = IpMappings;
        p.WrapMouse = WrapMouse;
        p.EasyMouse.Value = EasyMouse;
        p.MoveMouseRelatively = MoveMouseRelatively;
        p.ValidateRemoteMachineIP = ValidateRemoteMachineIP;
        p.SameSubnetOnly = SameSubnetOnly;
        p.ShareClipboard = false;
        p.TransferFile = false;
        p.ShowOriginalUI = false;
        p.UseService = true;
        p.FirstRun = false;
        p.DrawMouseCursor = false;
        p.BlockScreenSaverOnOtherMachines = false;
        p.KeyboardShortcutsEnabled = false;
        return settings;
    }
}
