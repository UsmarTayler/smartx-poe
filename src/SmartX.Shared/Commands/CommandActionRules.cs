namespace SmartX.Shared;

/// <summary>
/// The rules that tie an action to the kind of actuator that can perform it, and to the state it produces.
/// </summary>
/// <remarks>
/// In <c>SmartX.Shared</c> so the API, the simulator and the tests all read one definition. An actuator's state
/// is a single number: 1 for on or open, 0 for off or closed, and the level for a dimmer. One number keeps
/// "what state was it in" and "what state should it be in" directly comparable, which is what undo needs.
/// </remarks>
public static class CommandActionRules
{
    /// <summary>The kind of actuator an action is for.</summary>
    public static ActuatorKind KindFor(CommandAction action) => action switch
    {
        CommandAction.PumpOn or CommandAction.PumpOff => ActuatorKind.Pump,
        CommandAction.RelayOn or CommandAction.RelayOff => ActuatorKind.Relay,
        CommandAction.ValveOpen or CommandAction.ValveClose => ActuatorKind.Valve,
        CommandAction.SetLevel => ActuatorKind.Dimmer,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown command action."),
    };

    /// <summary>The state an action asks for.</summary>
    /// <exception cref="ArgumentException">A level action was given no level.</exception>
    public static double TargetValue(CommandAction action, double? level) => action switch
    {
        CommandAction.PumpOn or CommandAction.RelayOn or CommandAction.ValveOpen => 1,
        CommandAction.PumpOff or CommandAction.RelayOff or CommandAction.ValveClose => 0,
        CommandAction.SetLevel => level ?? throw new ArgumentException("SetLevel needs a level.", nameof(level)),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown command action."),
    };

    /// <summary>The action that puts an actuator of a kind into a recorded state.</summary>
    /// <remarks>
    /// This is how a restore command is built: from the state that was recorded, not from the opposite of
    /// the action that changed it.
    /// </remarks>
    public static CommandAction ActionToReach(ActuatorKind kind, double value) => kind switch
    {
        ActuatorKind.Pump => value >= 0.5 ? CommandAction.PumpOn : CommandAction.PumpOff,
        ActuatorKind.Relay => value >= 0.5 ? CommandAction.RelayOn : CommandAction.RelayOff,
        ActuatorKind.Valve => value >= 0.5 ? CommandAction.ValveOpen : CommandAction.ValveClose,
        ActuatorKind.Dimmer => CommandAction.SetLevel,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown actuator kind."),
    };

    /// <summary>Says why a command cannot be sent to a device, or null when it can.</summary>
    /// <param name="kind">The target's registered kind, or null if it was registered without one.</param>
    /// <param name="action">What is being asked.</param>
    /// <param name="level">The level, for <see cref="CommandAction.SetLevel"/>.</param>
    /// <param name="levelRange">The range the target is registered for, which bounds a level.</param>
    public static string? Validate(ActuatorKind? kind, CommandAction action, double? level, ValueRange levelRange)
    {
        if (!Enum.IsDefined(action))
        {
            return $"'{action}' is not a command. Use one of: {string.Join(", ", Enum.GetNames<CommandAction>())}.";
        }

        if (kind is null)
        {
            return "This device was registered without an actuator kind, so the gateway cannot tell which " +
                   "commands it understands. Register it with a kind to send it commands.";
        }

        var wanted = KindFor(action);

        if (kind != wanted)
        {
            return $"{action} is a {wanted} command, and this device is a {kind}.";
        }

        if (action == CommandAction.SetLevel)
        {
            if (level is null || double.IsNaN(level.Value) || double.IsInfinity(level.Value))
            {
                return "SetLevel needs a level.";
            }

            if (!levelRange.Contains(level.Value))
            {
                return $"Level {level} is outside this device's range {levelRange.Minimum:0.##} to {levelRange.Maximum:0.##}.";
            }
        }
        else if (level is not null)
        {
            return $"{action} does not take a level.";
        }

        return null;
    }
}
