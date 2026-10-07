using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// HTTP Basic on the OGC faces, over HTTPS — ADR-178, the owner's answer to Q-163.
/// </summary>
/// <remarks>
/// <b>Why it exists:</b> QGIS sent its token on GET and not on a WFS Transaction, so an editable layer could never be
/// saved. Basic is what QGIS's authentication manager sends on every request. Over plain HTTP it is refused rather
/// than used; a wrong one is a 401 with a challenge, so the client asks again rather than going on as a stranger.
/// </remarks>
[Collection("catalogue walk")]
public sealed class BasicOnTheOgcFacesTests : ArcGisClient
{
    private const string Capabilities = "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0";

    private static string Basic(string name, string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{name}:{password}"));

    private async Task<(HttpStatusCode Status, HttpResponseMessage Response, string Body)> SendAsync(string path, string? basic)
    {
        string root = await RequireServerAsync();
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));

        if (basic is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, response, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_name_and_password_signs_an_ogc_client_in_and_only_there()
    {
        string root = await RequireServerAsync();
        string? name = Environment.GetEnvironmentVariable(UserVariable);
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);
        Assert.False(string.IsNullOrEmpty(name) || string.IsNullOrEmpty(password), "No account is configured to sign in with.");

        (HttpStatusCode status, _, string body) = await SendAsync(Capabilities, Basic(name!, password!));

        if (root.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            // Plain HTTP: refused rather than used, and said why.
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Contains("HTTPS", body, StringComparison.Ordinal);
            return;
        }

        // Signed in: the editor is offered Transaction, which a stranger is not.
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("name=\"Transaction\"", body, StringComparison.Ordinal);
        (_, _, string anonymous) = await SendAsync(Capabilities, null);
        Assert.DoesNotContain("name=\"Transaction\"", anonymous, StringComparison.Ordinal);

        // A wrong password is a challenge, not a stranger's view.
        (HttpStatusCode wrong, HttpResponseMessage challenged, _) = await SendAsync(Capabilities, Basic(name!, password + "-not"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong);
        Assert.Contains(challenged.Headers.WwwAuthenticate, h => h.Scheme == "Basic");

        // Off the OGC faces Basic is not read at all: the ArcGIS surface answers as it does to anybody.
        (HttpStatusCode elsewhere, _, _) = await SendAsync("/rest/services?f=json", Basic(name!, password + "-not"));
        Assert.Equal(HttpStatusCode.OK, elsewhere);
    }
}
