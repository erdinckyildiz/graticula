using Graticula.Host.Ldap;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The name OpenSSL looks a trusted authority up by, written beside it for a directory over TLS — ADR-089 condition 1.
/// </summary>
/// <remarks>
/// <b>Found by CI, not here</b>: the Ubuntu runner's libldap is built with OpenSSL, which reads a trust directory by
/// <c>&lt;subject hash&gt;.0</c> only, so an authority the Windows build trusted was refused there. The expected hashes are
/// what <c>openssl x509 -hash -noout</c> printed for these two certificates; the first subject has capitals and runs
/// of spaces, the part the canonical form exists for.
/// </remarks>
public sealed class LdapTrustDirectoryTests
{
    private const string Spaced = """
-----BEGIN CERTIFICATE-----
MIIDczCCAlugAwIBAgIUBDr8OdCl2yXwpj7/3a7YaUj9oWUwDQYJKoZIhvcNAQEL
BQAwSTELMAkGA1UEBhMCVFIxHTAbBgNVBAoMFEdyYXRpY3VsYSAgVGVzdCAgIENB
MRswGQYDVQQDDBIgIFByb2JlIFJvb3QgICBDQSAwHhcNMjYxMDMxMTYxOTI3WhcN
MzYxMDI4MTYxOTI3WjBJMQswCQYDVQQGEwJUUjEdMBsGA1UECgwUR3JhdGljdWxh
ICBUZXN0ICAgQ0ExGzAZBgNVBAMMEiAgUHJvYmUgUm9vdCAgIENBIDCCASIwDQYJ
KoZIhvcNAQEBBQADggEPADCCAQoCggEBAJrF5qetM4d40aeTqAf6X79GQlBQgel3
Dxq+AMz9Okkk5NmCiX3b+e6zsuCcSccnR31ierYBKbb4G4+huZeDwgoudrQ+AV9B
Tw0E6LwOpDAQo5RKyuTZ7i1XCeyv8yXd8Cikbtb5VQsdNXEcSNerFcpyqjds0IlQ
X5/sXhvIHKI4IHnKEyD++Btaza0yotoS0DgigLrHT/JRsOhs1TJri9gslOiE2A8O
6bDuEIcVvd0Y4E7FTV0TeeZmW9U8WY6QZS9ICAvjw0x7Z77Hj/CM7VisV07Ede1I
laM9FPBTl4gP9sgJfVuRQGcS/3TqXpJ2WqVE/U7+pc9zqr+dqdtisL0CAwEAAaNT
MFEwHQYDVR0OBBYEFLPSkk5gCUmTZcU6q1KkTMxJrc82MB8GA1UdIwQYMBaAFLPS
kk5gCUmTZcU6q1KkTMxJrc82MA8GA1UdEwEB/wQFMAMBAf8wDQYJKoZIhvcNAQEL
BQADggEBAHRAOi5c1zZxq2JofFWWPj9pHbwBsrc6htfk4bg0UEDCWdlbE8w8KymS
+qjKOOvbUiY+KcLebQqQSij1bTRlTVV7IoirPAHogCJzHMGTJBY8m3qDKAqid8PK
ECC+Tn/yLQmHYW7mEVvkZNoe1Kan0+udtuyv5q0lc2BFek2Vye43TFGuV0wbO5qd
rSwNrYAXbmL41vLaE5A7AfeMkJWBtBWUAQsGDKELBTMos89UBRUeRDlBLt9oE7A6
AyhEg+MFqOiTPErjuwiYENTidu4+HxKe1DqtzVhiMA9qGb5x7mh1AOzmVmDKOQRY
qYF6JgwcvVWpcgHXoc4weRKSmRUZAaA=
-----END CERTIFICATE-----
""";

    private const string Plain = """
-----BEGIN CERTIFICATE-----
MIIDCTCCAfGgAwIBAgIUYEuoZd6Pg7vPelFzxHUa7eeC6C0wDQYJKoZIhvcNAQEL
BQAwFDESMBAGA1UEAwwJMTI3LjAuMC4xMB4XDTI2MTAzMTE2MTkyN1oXDTM2MTAy
ODE2MTkyN1owFDESMBAGA1UEAwwJMTI3LjAuMC4xMIIBIjANBgkqhkiG9w0BAQEF
AAOCAQ8AMIIBCgKCAQEAw5WfyJnKNJAmO2WfSqV9rq9YL+amifrGGlSZ0In/NcyD
bi1iF64f9a/cC6/QC4BfDqFY29TkdC2ElkWmdV7KG1muCZ2hWg3vDPKSlrx0tLD2
jf9e3MZ/+ekxp+zusKRgTV1D9bklEe66ktRNu6+655DuX6GXKkxhS+s0wWaTXl8N
tsEZOrgu8c+lVXaWa4eNQeRtYDBqi38zkyoFyJBdvzzY0trTfxw5T+D3MTUVBxKN
jklNiX6TX3owjfW1muTF6m/hBdtfW6SxbPjBifkoPVFwEQ7QedMd4DfvgITkfjZR
jeONdttpsW4zYZV1MGFfVjj3C6ivtLiOSLhsYJ/X1wIDAQABo1MwUTAdBgNVHQ4E
FgQUqLAyfIzgs8/8kcTTiW74gRVZKx0wHwYDVR0jBBgwFoAUqLAyfIzgs8/8kcTT
iW74gRVZKx0wDwYDVR0TAQH/BAUwAwEB/zANBgkqhkiG9w0BAQsFAAOCAQEApbuL
Kw67gPaEZMvYUNo0UG9mdpc0hCIWz0el6bu6O0fk91gzCctR536RcEdr1SAMgP/s
Vk3gVNmtq68wOqPg8+LA1u7PGjDKd9mFqV6UVeZYWGlXKSGpXevTgzcI+x9wbbCg
mfDSodd9GRi9mV0FPGm7ziPoZy25ZWo1UlSX1v4vMBvFGeMbUY6kjtu//2CSQeqX
I4GCiZPzfrakLao3WYpem1vr0YGgICMWiDabHlW4uYIewDvGkOBI0TV6t8uE6Y37
cyInxcOXEb2Or6kHG7j7BLhXIRxVlf1Dd4v2rn9t++1M+VakiUG3u1VbsQusRUVF
U4avppYAejXWO8yeXA==
-----END CERTIFICATE-----
""";

    /// <summary>The hash is OpenSSL's, canonical form and byte order included.</summary>
    [Theory]
    [InlineData(Spaced, "59c6c636")]
    [InlineData(Plain, "88d0bdcb")]
    public void The_subject_hash_is_the_one_openssl_prints(string pem, string expected) =>
        Assert.Equal(expected, LdapDirectory.SubjectHash(pem));
}
