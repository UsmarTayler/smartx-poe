namespace SmartX.Shared;

/// <summary>
/// What it costs to send a packet over one link: a radio cost made worse by how little the weaker end can be trusted.
/// </summary>
/// <remarks>
/// <c>cost = rssiCost × (1 + penaltyFactor × (1 − lowerTrust))</c>
/// <list type="bullet">
/// <item><description>
/// <b>rssiCost</b> maps signal strength linearly onto 1 to 10: -30 dBm (strong) costs 1 and -90 dBm (weak) costs 10. A weak link
/// is lossy and forces retransmissions, and each retransmission costs power and airtime, so a weaker link is a more expensive one.
/// Readings outside the range are clamped to it.
/// </description></item>
/// <item><description>
/// <b>lowerTrust</b> is the lower trust score of the link's two ends, 0 to 1. A route is only as reliable as its least reliable hop,
/// and a link has two ends, so the worse one decides.
/// </description></item>
/// <item><description>
/// <b>penaltyFactor</b> says how much that matters. At 0 trust is ignored and the cost is the radio cost. At the default of 4 a
/// link whose weaker end has no trust at all costs five times its radio cost, so a route through a shaky device is only chosen if
/// there is no reasonable way round it. A device that is in the Fault band is not given a very high cost: it is left out of the
/// graph entirely, so no route is ever planned through it.
/// </description></item>
/// </list>
/// The result is never below 1, so the cost of a route never falls as it gets longer, which the minimum spanning tree relies on.
/// </remarks>
public static class LinkCost
{
    /// <summary>The strongest signal the scale knows, in dBm.</summary>
    public const int StrongestRssi = -30;

    /// <summary>The weakest signal the scale knows, in dBm.</summary>
    public const int WeakestRssi = -90;

    /// <summary>The cost of the strongest link.</summary>
    public const double BestRadioCost = 1;

    /// <summary>The cost of the weakest link.</summary>
    public const double WorstRadioCost = 10;

    /// <summary>The default for how hard low trust pushes a cost up.</summary>
    public const double DefaultPenaltyFactor = 4;

    /// <summary>The radio cost of a signal strength, 1 to 10, linear in dBm.</summary>
    public static double RssiCost(int rssi)
    {
        var clamped = Math.Clamp(rssi, WeakestRssi, StrongestRssi);
        var weakness = (double)(StrongestRssi - clamped) / (StrongestRssi - WeakestRssi);

        return BestRadioCost + (weakness * (WorstRadioCost - BestRadioCost));
    }

    /// <summary>The cost of a link: its radio cost, multiplied up by the lack of trust in its weaker end.</summary>
    /// <param name="rssi">The link's signal strength in dBm.</param>
    /// <param name="lowerTrust">The lower of the two ends' trust scores, 0 to 1. Values outside the range are clamped.</param>
    /// <param name="penaltyFactor">How hard low trust pushes the cost up. Not negative.</param>
    public static double Cost(int rssi, double lowerTrust, double penaltyFactor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(penaltyFactor);

        return RssiCost(rssi) * (1 + (penaltyFactor * (1 - Math.Clamp(lowerTrust, 0, 1))));
    }
}
