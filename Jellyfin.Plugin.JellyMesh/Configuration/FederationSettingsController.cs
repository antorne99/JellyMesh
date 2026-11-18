using System;
using System.Net.Mime;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The global settings in one object, for administrators only (§3.2). They replace the stock plugin configuration
/// endpoint, which answers 404 since the plugin has no Jellyfin configuration.
/// </summary>
[ApiController]
[Route("JellyMesh/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class FederationSettingsController : ControllerBase
{
    /// <summary>The refusal of a save while the plugin database is suspended.</summary>
    internal const string StoreSuspended = "The JellyMesh database cannot be written now, so the settings were not saved.";

    private readonly IPluginConfigurationStore _store;
    private readonly IFederatedRootSource _rootSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationSettingsController"/> class.
    /// </summary>
    /// <param name="store">The configuration store.</param>
    /// <param name="rootSource">The federated folder set by the environment.</param>
    public FederationSettingsController(IPluginConfigurationStore store, IFederatedRootSource rootSource)
    {
        _store = store;
        _rootSource = rootSource;
    }

    /// <summary>
    /// Gets every global setting, with those an environment variable decides.
    /// </summary>
    /// <returns>The settings.</returns>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginSettings> GetSettings()
    {
        FixedSetting[] fixedByEnvironment = EnvironmentRoot() is null
            ? []
            : [new FixedSetting(nameof(PluginSettings.FederatedFolderPath), EnvironmentFederatedRootSource.VariableName)];
        try
        {
            return _store.Read(config => PluginSettings.From(config, fixedByEnvironment));
        }
        catch (StoreSuspendedException)
        {
            // Defaults saved back from the page would replace the real settings.
            return StoreUnavailableFilter.Unavailable();
        }
    }

    /// <summary>
    /// Saves the settings the request holds, all or none. A value out of range, or other than the one an environment
    /// variable decides, is refused with the name of its field (D20).
    /// </summary>
    /// <param name="request">The settings; one left out keeps its value.</param>
    /// <returns>The settings saved.</returns>
    [HttpPut("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PluginSettings> SetSettings([FromBody] SettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = EnvironmentRoot();
        if (root is not null && request.FederatedFolderPath is { } path && !string.Equals(path.Trim(), root, StringComparison.Ordinal))
        {
            return BadRequest(new SettingRefusal(
                nameof(PluginSettings.FederatedFolderPath),
                $"{nameof(PluginSettings.FederatedFolderPath)} is set by {EnvironmentFederatedRootSource.VariableName} on the server, so it cannot be changed here."));
        }

        // Each rule looks at one setting, so the request on the defaults finds every value it breaks.
        var candidate = new PluginConfiguration();
        request.ApplyTo(candidate);
        try
        {
            candidate.Validate();
        }
        catch (ArgumentException exception)
        {
            var field = exception.ParamName ?? string.Empty;
            return BadRequest(new SettingRefusal(field, exception.Message.Replace(" (Parameter '" + field + "')", string.Empty, StringComparison.Ordinal)));
        }

        try
        {
            // A failed save puts every setting back, so the page and the schedule never run on unsaved values.
            SettingsRequest? previous = null;
            _store.Update(
                config =>
                {
                    previous = SettingsRequest.From(config);
                    request.ApplyTo(config);
                },
                config => previous?.ApplyTo(config));
        }
        catch (StoreSuspendedException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new FailureResponse(RemoteFailure.None, StoreSuspended));
        }

        return GetSettings();
    }

    private string? EnvironmentRoot() => PluginConfiguration.FederatedRootOf(_rootSource.GetFederatedRoot());
}
