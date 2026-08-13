namespace GisServer.Platform.Identity;

/// <summary>The three kinds of principal (ADR-015 §2).</summary>
public enum PrincipalKind
{
    /// <summary>
    /// A person. Owns items and holds roles.
    /// </summary>
    User = 1,

    /// <summary>
    /// A machine, authenticating with an API key or a client certificate.
    /// <b>Never owns items</b> — ownership carries sharing decisions, and a
    /// machine has no judgement about them (ADR-015 §7).
    /// </summary>
    Service = 2,

    /// <summary>
    /// Unauthenticated access.
    /// </summary>
    /// <remarks>
    /// <b>A principal, not the absence of one.</b> Open data portals are a normal
    /// deployment of a GIS server, and modelling anonymous as <em>no identity</em>
    /// scatters <c>if (user is null)</c> through every authorization check, which
    /// is where bugs live. It has a name, can hold roles, and can be granted a
    /// layer; refusing anonymous access is then configuration rather than a
    /// special case (ADR-015 §2a).
    /// </remarks>
    Anonymous = 3,
}
