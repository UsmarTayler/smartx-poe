using MudBlazor;
using SmartX.Shared;

namespace SmartX.Client.Theme;

/// <summary>
/// How a command's state is shown: an icon, a word and a colour, always all three.
/// </summary>
/// <remarks>
/// The same rule as the trust bands (see <see cref="TrustBandStyle"/>): colour is never the only encoding. Pending,
/// delivered and acknowledged are three steps of one journey and have to be told apart at a glance by someone who cannot
/// tell amber from green, so each has its own icon shape and its own word. The colours are the same hexes the trust bands
/// use for the same meanings: green for done, red for failed, amber for uncertain, grey for not yet.
/// </remarks>
public readonly record struct CommandStateStyle(string Colour, string Icon, string Label)
{
    public static CommandStateStyle For(CommandState state) => state switch
    {
        CommandState.Pending => new("#6b6a66", Icons.Material.Filled.HourglassEmpty, "Pending"),
        CommandState.Delivered => new("#2f5d8a", Icons.Material.Filled.LocalShipping, "Delivered"),
        CommandState.Acknowledged => new("#0ca30c", Icons.Material.Filled.CheckCircle, "Acknowledged"),
        CommandState.Failed => new("#d03b3b", Icons.Material.Filled.Error, "Failed"),
        CommandState.TimedOut => new("#fab219", Icons.Material.Filled.TimerOff, "Timed out"),
        CommandState.Cancelled => new("#6b6a66", Icons.Material.Filled.Cancel, "Cancelled"),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown command state."),
    };
}

/// <summary>How an alert's kind is shown: an icon, a word and a colour.</summary>
public readonly record struct AlertKindStyle(string Colour, string Icon, string Label)
{
    public static AlertKindStyle For(AlertKind kind) => kind switch
    {
        AlertKind.Fault => new("#d03b3b", Icons.Material.Filled.Error, "Fault"),
        AlertKind.Disconnected => new("#d03b3b", Icons.Material.Filled.SignalWifiOff, "Disconnected"),
        AlertKind.CommandTimedOut => new("#fab219", Icons.Material.Filled.TimerOff, "Command timed out"),
        AlertKind.OutOfRange => new("#fab219", Icons.Material.Filled.TrendingUp, "Out of range"),
        AlertKind.TypeViolation => new("#fab219", Icons.Material.Filled.Rule, "Wrong type"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown alert kind."),
    };
}
