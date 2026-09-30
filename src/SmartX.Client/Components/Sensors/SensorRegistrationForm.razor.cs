using Microsoft.AspNetCore.Components;
using MudBlazor;
using SmartX.Client.Services;
using SmartX.Shared;

namespace SmartX.Client.Components.Sensors;

/// <summary>
/// The registration form's state, parameters and submission.
/// </summary>
/// <remarks>
/// Split into code-behind so the markup reads as a form and the behaviour reads as C#. This file holds
/// what the form is and when it validates; <c>SensorRegistrationForm.Fields.cs</c> holds the rule
/// each field runs and what editing each field clears.
/// </remarks>
public partial class SensorRegistrationForm
{
    // Field names come from the request type itself, so renaming a property breaks the build here
    // rather than silently detaching the server's errors from the fields they belong to.
    private const string MacField = nameof(SensorRegistrationRequest.MacAddress);
    private const string RoomField = nameof(SensorRegistrationRequest.Room);
    private const string ZoneField = nameof(SensorRegistrationRequest.ZoneId);
    private const string NodeIdField = nameof(SensorRegistrationRequest.NodeId);
    private const string UnitField = nameof(SensorRegistrationRequest.Unit);
    private const string MinimumField = nameof(SensorRegistrationRequest.Minimum);
    private const string PayloadField = nameof(SensorRegistrationRequest.PayloadKind);
    private const string HeartbeatField = nameof(SensorRegistrationRequest.HeartbeatIntervalSeconds);
    private const string ActuatorKindField = nameof(SensorRegistrationRequest.ActuatorKind);

    private MudForm? _form;
    private MudNumericField<double?>? _minimumField;
    private MudSelect<TelemetryPayloadKind>? _payloadField;
    private MudSelect<ActuatorKind?>? _actuatorKindField;
    private bool _isValid;
    private ApiProblem? _appliedProblem;
    private bool _revalidate;
    private bool _resetValidationPending;
    private Dictionary<string, string[]> _serverErrors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The zones to choose from, flattened parent before child.</summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<ZoneOption> Zones { get; set; } = [];

    /// <summary>Whether a submission is in flight, which disables the button.</summary>
    [Parameter]
    public bool Busy { get; set; }

    /// <summary>What the server rejected on the last attempt, if anything.</summary>
    [Parameter]
    public ApiProblem? ServerProblem { get; set; }

    /// <summary>Raised with a complete request once every field passes validation.</summary>
    [Parameter]
    public EventCallback<SensorRegistrationRequest> OnSubmit { get; set; }

    /// <summary>The draft being edited.</summary>
    /// <remarks>
    /// Owned by the page's scoped service rather than by this component, so a half-filled form survives
    /// navigating away and back. A component's fields die with the component; a scoped service does not.
    /// </remarks>
    [Parameter, EditorRequired]
    public SensorRegistrationModel Model { get; set; } = new();

    private IReadOnlyList<string> ZoneViolations =>
        _serverErrors.TryGetValue(ZoneField, out var messages) ? messages : [];

    /// <summary>Clears the form after a successful registration, keeping the location for the next device.</summary>
    public Task ResetAfterSuccessAsync()
    {
        Model.ClearForNextSensor();
        _serverErrors.Clear();

        // The validation reset is deferred until after the render that empties the MAC box. Done here,
        // before that render, the field would see its value change to empty and flag "A MAC address is
        // required" in red immediately after a successful registration.
        if (_form is not null)
        {
            _resetValidationPending = true;
        }

        StateHasChanged();

        return Task.CompletedTask;
    }

    protected override void OnParametersSet()
    {
        // The page hands over a new problem instance per attempt, so a reference check is enough to
        // tell a fresh rejection from a re-render of the same one.
        if (ReferenceEquals(ServerProblem, _appliedProblem))
        {
            return;
        }

        _appliedProblem = ServerProblem;
        _serverErrors = ServerProblem is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(ServerProblem.FieldErrors, StringComparer.OrdinalIgnoreCase);
        _revalidate = _serverErrors.Count > 0;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Runs after the render that emptied the MAC box, for the reason given in ResetAfterSuccessAsync.
        if (_resetValidationPending && _form is not null)
        {
            _resetValidationPending = false;
            await _form.ResetValidationAsync();
        }

        // Re-validating after the render that applied the server's errors makes each field pick up its
        // own message through the same validation function that runs as the user types. There is no
        // second, separate way for an error to reach a field.
        if (_revalidate && _form is not null)
        {
            _revalidate = false;
            await _form.ValidateAsync();
        }
    }

    private async Task SubmitAsync()
    {
        if (_form is null)
        {
            return;
        }

        // Validation runs on submit as well as on typing, so a field the user never touched — and so
        // never triggered — is still caught before anything is sent.
        //
        // The previous attempt's server errors are cleared first. They describe the values submitted
        // last time, and left in place they keep the form invalid after the user has fixed the real cause
        // in a different field — a range that broke a zone rule, where the error sits on the zone but the
        // fix is to the range. Resubmitting asks the server again.
        _serverErrors.Clear();
        await _form.ValidateAsync();

        if (_form.IsValid)
        {
            await OnSubmit.InvokeAsync(Model.ToRequest());
        }
    }

    private string? ServerError(string field) =>
        _serverErrors.TryGetValue(field, out var messages) && messages.Length > 0 ? messages[0] : null;
}
