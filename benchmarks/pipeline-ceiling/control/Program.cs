// The control for D-249.
//
// D-249 says "this server's request pipeline stops scaling at sixteen concurrent callers".
// Every candidate excluded so far was excluded by removing something from Graticula --
// TLS, the request log, authentication, the data source. None of them removed Graticula.
//
// This is the missing control: a different server, on the same machine, over the same
// protocol, answering the same 182 bytes at the same path, driven by the same k6 script
// at the same concurrencies. Whatever ceiling it hits is the machine's, and only the gap
// between the two is Graticula's.
//
// It deliberately has NO middleware: no routing beyond one endpoint, no logging, no
// authentication, no HTTPS redirection. That is the point -- it is the floor.

using System.Text;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

// Quiet, because a console write per request would make this measure the console.
builder.Logging.ClearProviders();

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenLocalhost(8555, listen => listen.UseHttps());
});

WebApplication app = builder.Build();

// Byte-for-byte what the dev server answers, read from it with curl rather than retyped:
// 182 bytes, so the response size is not a difference between the two.
byte[] body = Encoding.UTF8.GetBytes(
    "{\"currentVersion\":10.81,\"fullVersion\":\"10.81.0\",\"authInfo\":{\"isTokenBasedSecurity\":true,"
    + "\"tokenServicesUrl\":\"https://127.0.0.1:8443/rest/generateToken\",\"shortLivedTokenValidity\":720}}");

app.MapGet("/rest/info", (HttpContext context) =>
{
    context.Response.ContentType = "application/json";
    context.Response.ContentLength = body.Length;
    return context.Response.Body.WriteAsync(body, 0, body.Length);
});

app.Run();
