using System;
using System.Collections.Generic;
using BassRelay.Models;

namespace BassRelay.Audio;

public static class RoutingPolicy
{
    public const string DefaultDeviceMessage = "DefaultDeviceBlocked";

    /// <summary>Claims enabled destinations in display order and returns a host-localized message key.</summary>
    public static string? GetBlockReason(ShakerSettings shaker, string? sourceDeviceId, ISet<string> claimedTargets)
    {
        if (shaker is null) throw new ArgumentNullException(nameof(shaker));
        if (claimedTargets is null) throw new ArgumentNullException(nameof(claimedTargets));
        if (string.IsNullOrWhiteSpace(shaker.DeviceId)) return null;
        if (string.Equals(shaker.DeviceId, sourceDeviceId, StringComparison.OrdinalIgnoreCase)) return DefaultDeviceMessage;
        if (shaker.Enabled && !claimedTargets.Add(shaker.DeviceId!))
            return "DuplicateDeviceBlocked";
        return null;
    }
}
