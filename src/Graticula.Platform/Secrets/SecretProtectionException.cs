using System;

namespace Graticula.Platform.Secrets;

/// <summary>Thrown when a secret cannot be sealed or opened.</summary>
public sealed class SecretProtectionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SecretProtectionException()
        : base("The secret could not be protected or unprotected.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public SecretProtectionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SecretProtectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
