using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;

namespace Graticula.Platform.Identity;

/// <summary>Whom the organisation requires a second factor of — ADR-201 condition 2.</summary>
public enum SecondFactorRequirement
{
    /// <summary>Nobody: a member turns one on if they choose. The default, by owner decision 2026-10-19.</summary>
    Off,

    /// <summary>Every member holding the administrator role.</summary>
    Administrators,

    /// <summary>Every member who signs in with a password here.</summary>
    Everyone,
}

/// <summary>
/// The organisation's second-factor requirement, kept as one server setting — ADR-201 condition 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off unless an administrator turns it on — owner decision 2026-10-19.</b> A required second factor ends sign-in
/// by password alone for whoever it covers: <c>generateToken</c>, HTTP Basic and every script that signs in as an
/// administrator stop, and an API key cannot take their place, since a machine never administers (ADR-200). An
/// organisation that turns it on takes that cost knowingly; one that never asked is not handed it by an upgrade.
/// </para>
/// <para>
/// <b>Only an account with a password here is covered.</b> One from an identity provider or a directory has its second
/// step there, as ADR-201 §2 already says of turning one on.
/// </para>
/// </remarks>
public sealed class SecondFactorPolicy
{
    /// <summary>The setting's name in <c>server_setting</c>.</summary>
    public const string Setting = "secondFactor.required";

    private readonly IServerSettingStore _settings;

    /// <summary>Creates the policy over the server's settings.</summary>
    /// <param name="settings">Where it is kept.</param>
    public SecondFactorPolicy(IServerSettingStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>The requirement as stored; <see cref="SecondFactorRequirement.Off"/> when none is.</summary>
    public async Task<SecondFactorRequirement> ReadAsync(CancellationToken cancellationToken) =>
        Parse((await _settings.ReadAsync(Setting, cancellationToken).ConfigureAwait(false))?.Value)
        ?? SecondFactorRequirement.Off;

    /// <summary>Stores the requirement; off removes the setting, so a store that never had one reads the same.</summary>
    public Task WriteAsync(SecondFactorRequirement requirement, Guid? changedBy, CancellationToken cancellationToken) =>
        _settings.WriteAsync(Setting, requirement == SecondFactorRequirement.Off ? null : Name(requirement), changedBy, cancellationToken);

    /// <summary>Whether the requirement covers a member, given whether they hold the administrator role.</summary>
    public static bool Covers(SecondFactorRequirement requirement, bool administrator) => requirement switch
    {
        SecondFactorRequirement.Everyone => true,
        SecondFactorRequirement.Administrators => administrator,
        _ => false,
    };

    /// <summary>The wire name: <c>off</c>, <c>administrators</c>, <c>everyone</c>.</summary>
    public static string Name(SecondFactorRequirement requirement) => requirement switch
    {
        SecondFactorRequirement.Administrators => "administrators",
        SecondFactorRequirement.Everyone => "everyone",
        _ => "off",
    };

    /// <summary>A wire name read back, or null when it is not one.</summary>
    public static SecondFactorRequirement? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "off" => SecondFactorRequirement.Off,
        "administrators" => SecondFactorRequirement.Administrators,
        "everyone" => SecondFactorRequirement.Everyone,
        _ => null,
    };
}
