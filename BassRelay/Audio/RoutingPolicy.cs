using System;
using System.Collections.Generic;
using BassRelay.Models;
using BassRelay.Services;

namespace BassRelay.Audio;

public static class RoutingPolicy
{
    public static string DefaultDeviceMessage => Localization.Text("DefaultDeviceBlocked");

    /// <summary>Claims enabled destinations in display order so each endpoint receives one stream.</summary>
    public static string? GetBlockReason(ShakerSettings shaker, string? sourceDeviceId, ISet<string> claimedTargets)
    {
        ArgumentNullException.ThrowIfNull(shaker);
        ArgumentNullException.ThrowIfNull(claimedTargets);
        if (string.IsNullOrWhiteSpace(shaker.DeviceId)) return null;
        if (string.Equals(shaker.DeviceId, sourceDeviceId, StringComparison.OrdinalIgnoreCase)) return DefaultDeviceMessage;
        if (shaker.Enabled && !claimedTargets.Add(shaker.DeviceId))
            return Localization.Text("DuplicateDeviceBlocked");
        return null;
    }
}
