using MudBlazor;
using SmartX.Shared;

namespace SmartX.Client.CommandStream;

/// <summary>One thing an operator can ask an actuator to do, as a button.</summary>
/// <param name="Action">What is sent.</param>
/// <param name="Label">The words on the button.</param>
/// <param name="Icon">The icon on the button.</param>
public sealed record CommandChoice(CommandAction Action, string Label, string Icon);

/// <summary>
/// What the page offers for each kind of actuator.
/// </summary>
/// <remarks>
/// <b>A mismatched action is impossible from the UI.</b> The controls are made from the device's kind, and only from
/// it: a pump gets on and off, a valve gets open and close, a dimmer gets a level. There is nothing to choose wrongly,
/// so the server's refusal of a valve command sent to a pump never has to be shown. The server still refuses it,
/// because the UI is not the only client; this is the UI not making the mistake possible.
/// </remarks>
public static class CommandChoices
{
    public static IReadOnlyList<CommandChoice> For(ActuatorKind kind) => kind switch
    {
        ActuatorKind.Pump =>
        [
            new(CommandAction.PumpOn, "Pump on", Icons.Material.Filled.PlayArrow),
            new(CommandAction.PumpOff, "Pump off", Icons.Material.Filled.Stop),
        ],
        ActuatorKind.Relay =>
        [
            new(CommandAction.RelayOn, "Relay on", Icons.Material.Filled.ToggleOn),
            new(CommandAction.RelayOff, "Relay off", Icons.Material.Filled.ToggleOff),
        ],
        ActuatorKind.Valve =>
        [
            new(CommandAction.ValveOpen, "Open valve", Icons.Material.Filled.LockOpen),
            new(CommandAction.ValveClose, "Close valve", Icons.Material.Filled.Lock),
        ],
        ActuatorKind.Dimmer =>
        [
            new(CommandAction.SetLevel, "Set level", Icons.Material.Filled.Tune),
        ],
        _ => [],
    };

    /// <summary>A sentence for a command: what it does and to what.</summary>
    public static string Describe(Command command) => command.Action switch
    {
        CommandAction.SetLevel => $"Set level to {command.Level:0.##}",
        _ => For(command.Kind).FirstOrDefault(choice => choice.Action == command.Action)?.Label ?? command.Action.ToString(),
    };

    /// <summary>Every action a zone-wide command may carry, with its words.</summary>
    public static IReadOnlyList<CommandChoice> ForZone { get; } =
    [
        .. Enum.GetValues<ActuatorKind>().SelectMany(For),
    ];
}
