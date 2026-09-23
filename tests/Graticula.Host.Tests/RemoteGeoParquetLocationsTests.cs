using System;
using System.IO;
using System.Net;
using Graticula.Platform.Admin;
using Graticula.Providers.DuckDb;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// What an administrator may register as remote GeoParquet — ADR-067 §5.2 — and what the server keeps.
/// </summary>
/// <remarks>
/// <b>Mostly refusals</b>, because each field here is bound into a DuckDB setting and every rule is
/// what makes that value the right kind of value. Hosts are literal addresses or AWS buckets, which
/// the address check does not look up; the one lookup is of <c>bucket.8.8.8.8</c>, a name that cannot
/// exist, so the suite never depends on what a resolver answers for a real one.
/// </remarks>
public sealed class RemoteGeoParquetLocationsTests
{
    private static RemoteLocationRequest Request(
        string? url,
        string? region = null,
        string? endpoint = null,
        string? key = null,
        string? secret = null,
        string? style = null,
        bool? ssl = null) => new(url, region, endpoint, key, secret, style, ssl);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("::7f00:1")]
    [InlineData("198.18.0.1")]
    public void An_address_inside_a_network_is_private(string address) =>
        Assert.True(RemoteGeoParquetLocations.IsPrivate(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("52.218.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    public void A_public_address_is_not(string address) =>
        Assert.False(RemoteGeoParquetLocations.IsPrivate(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("http://8.8.8.8/a/roads.parquet", "Plain http")]
    [InlineData("ftp://8.8.8.8/a/roads.parquet", "s3:// or https://")]
    [InlineData("file:///etc/passwd", "s3:// or https://")]
    [InlineData("/data/roads.parquet", "s3:// or https://")]
    [InlineData("https://8.8.8.8/a/", "one .parquet file")]
    [InlineData("https://8.8.8.8/a/roads.parquet?X-Amz-Signature=abc", "query string")]
    [InlineData("https://8.8.8.8/a/*.parquet", "pattern")]
    [InlineData("s3://bucket/{a,b}/", "pattern")]
    [InlineData("s3://bucket/a'/", "pattern")]
    [InlineData("https://user:pass@8.8.8.8/a/roads.parquet", "user name")]
    [InlineData("https://8.8.8.8/a/roads.csv", "neither a prefix")]
    [InlineData("s3://B/prefix/", "bucket name")]
    [InlineData("s3://bucket_with_underscores/prefix/", "bucket name")]
    [InlineData("", "required")]
    public void A_location_this_server_should_not_read_is_refused(string url, string because)
    {
        Assert.False(RemoteGeoParquetLocations.TryLocate(Request(url), allowPrivate: false, out string? locator, out string? why));
        Assert.Null(locator);
        Assert.Contains(because, why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("us west", null, null, null, null, "region name")]
    [InlineData(null, "https://minio.example.org", null, null, null, "host or host:port")]
    [InlineData(null, "minio.example.org/path", null, null, null, "host or host:port")]
    [InlineData(null, null, "AKIAEXAMPLE", null, null, "come together")]
    [InlineData(null, null, null, "secret", null, "come together")]
    [InlineData(null, null, null, null, "virtual", "vhost or path")]
    public void Bucket_fields_are_checked_one_by_one(
        string? region, string? endpoint, string? key, string? secret, string? style, string because)
    {
        Assert.False(RemoteGeoParquetLocations.TryLocate(
            Request("s3://bucket/prefix/", region, endpoint, key, secret, style), allowPrivate: true, out _, out string? why));

        Assert.Contains(because, why, StringComparison.Ordinal);
    }

    [Fact]
    public void An_https_file_refuses_bucket_fields()
    {
        Assert.False(RemoteGeoParquetLocations.TryLocate(
            Request("https://8.8.8.8/a/roads.parquet", region: "us-west-2"), allowPrivate: false, out _, out string? why));

        Assert.Contains("belong to an s3:// location", why, StringComparison.Ordinal);
    }

    [Fact]
    public void An_endpoint_on_a_private_address_is_refused_unless_the_deployment_allows_it()
    {
        RemoteLocationRequest minio = Request("s3://bucket/prefix/", "us-east-1", "10.0.0.5:9000", "AKIAEXAMPLE", "s3cr3t", "path", false);

        Assert.False(RemoteGeoParquetLocations.TryLocate(minio, allowPrivate: false, out _, out string? why));
        Assert.Contains("10.0.0.5", why, StringComparison.Ordinal);
        Assert.Contains("Graticula:RemoteDataAllowPrivate", why, StringComparison.Ordinal);

        Assert.True(RemoteGeoParquetLocations.TryLocate(minio, allowPrivate: true, out string? locator, out why), why);
        Assert.NotNull(locator);
    }

    [Fact]
    public void A_bucket_shaped_like_an_address_is_refused()
    {
        // The security review's H1 began with bucket `169.254.169.254` on endpoint `nip.io`.
        Assert.False(RemoteGeoParquetLocations.TryLocate(
            Request("s3://169.254.169.254/latest/", endpoint: "nip.io", ssl: false), allowPrivate: false, out _, out string? why));

        Assert.Contains("shaped like an IP address", why, StringComparison.Ordinal);
    }

    [Fact]
    public void With_virtual_hosts_the_bucket_s_own_host_is_checked_too()
    {
        // DuckDB connects to `bucket.endpoint` unless the style is path; that host is the one checked.
        // `bucket.8.8.8.8` resolves nowhere, so vhost is refused and path — which connects to 8.8.8.8 — is not.
        Assert.False(RemoteGeoParquetLocations.TryLocate(
            Request("s3://bucket/prefix/", endpoint: "8.8.8.8", style: "vhost"), allowPrivate: false, out _, out string? why));
        Assert.Contains("bucket.8.8.8.8", why, StringComparison.Ordinal);

        Assert.True(RemoteGeoParquetLocations.TryLocate(
            Request("s3://bucket/prefix/", endpoint: "8.8.8.8", style: "path"), allowPrivate: false, out _, out why), why);
    }

    [Fact]
    public void A_request_s_text_never_carries_a_secret()
    {
        const string Secret = "sentinel-secret-5d1c";

        Assert.DoesNotContain(Secret, new RemoteLocationRequest("s3://b/p/", null, null, "AKIAEXAMPLE", Secret, null, null).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, new DataSourceRequest("n", null, Password: Secret).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, new DataSourceRequest("n", null, Kind: "geoparquet-remote", Url: "s3://b/p/", AccessKeyId: "AKIA", SecretAccessKey: Secret).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, new DataSourceRequest("n", $"Host=db;Password={Secret}").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_https_host_on_loopback_is_refused()
    {
        Assert.False(RemoteGeoParquetLocations.TryLocate(
            Request("https://127.0.0.1/a/roads.parquet"), allowPrivate: false, out _, out string? why));

        Assert.Contains("private, loopback or link-local", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bucket_without_a_trailing_slash_is_its_root_prefix()
    {
        Assert.True(RemoteGeoParquetLocations.TryLocate(Request("s3://overturemaps-us-west-2"), false, out string? locator, out string? why), why);

        Assert.Equal("s3://overturemaps-us-west-2/", RemoteGeoParquetLocations.Parse(locator!).Location);
    }

    [Fact]
    public void The_stored_locator_names_its_kind_and_keeps_everything_it_was_given()
    {
        Assert.True(RemoteGeoParquetLocations.TryLocate(
            Request("s3://bucket/prefix/", "eu-west-1", "10.0.0.5:9000", "AKIAEXAMPLE", "s3cr3t value", "path", false),
            allowPrivate: true, out string? locator, out string? why), why);

        Assert.True(GeoParquetLocator.Is(locator));
        Assert.True(GeoParquetLocator.IsRemote(locator));
        Assert.Equal(DataSourceKinds.GeoParquetRemote, GeoParquetLocator.KindOf(locator));
        Assert.Throws<ArgumentException>(() => GeoParquetLocator.FolderOf(locator!));

        RemoteGeoParquet remote = RemoteGeoParquetLocations.Parse(locator!);

        Assert.Equal(
            new RemoteGeoParquet("s3://bucket/prefix/", "eu-west-1", "10.0.0.5:9000", "AKIAEXAMPLE", "s3cr3t value", "path", UseSsl: false),
            remote);

        // What a sentence, an audit line or the listing shows of it: the location alone.
        Assert.Equal("s3://bucket/prefix/", GeoParquetSources.LocationOf(locator!));
        Assert.DoesNotContain("s3cr3t", remote.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_locator_is_not_a_remote_one_and_the_reverse()
    {
        string folder = GeoParquetLocator.For("/data/geoparquet/istanbul");

        Assert.True(GeoParquetLocator.Is(folder));
        Assert.False(GeoParquetLocator.IsRemote(folder));
        Assert.Equal(DataSourceKinds.GeoParquet, GeoParquetLocator.KindOf(folder));
        Assert.Equal(DataSourceKinds.PostGis, GeoParquetLocator.KindOf("Host=db;Database=gis"));
    }

    [Fact]
    public void Without_httpfs_remote_locations_are_off_and_say_which_setting()
    {
        using GeoParquetSources none = new(null, "128MB", 1);
        using GeoParquetSources elsewhere = new(null, "128MB", 1, Path.Combine(Path.GetTempPath(), "no-duckdb-extensions-" + Guid.NewGuid().ToString("n")));

        foreach (GeoParquetSources sources in new[] { none, elsewhere })
        {
            Assert.False(sources.RemoteEnabled);
            Assert.False(sources.TryLocateRemote(Request("s3://bucket/prefix/"), out string? locator, out string? why));
            Assert.Null(locator);
            Assert.Contains("Graticula:DuckDbExtensions", why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_stored_remote_locator_is_not_opened_when_httpfs_is_gone()
    {
        Assert.True(RemoteGeoParquetLocations.TryLocate(Request("s3://bucket/prefix/"), false, out string? locator, out _));

        using GeoParquetSources sources = new(null, "128MB", 1);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => sources.FolderFor(locator!));
        Assert.Contains("Graticula:DuckDbExtensions", refused.Message, StringComparison.Ordinal);

        Assert.Equal(ProbeOutcome.CannotConnect, sources.Probe(locator!).Outcome);
    }

    [Fact]
    public void A_remote_source_that_failed_to_open_is_not_asked_again_at_once()
    {
        // V-74, the fourth ArcGIS review: a lapsed MotherDuck account took 3.5 s to fail on every request, and every
        // listing that describes the layer paid it again. The second ask inside the cooling period is answered at
        // once, as the breaker answers, and still says why.
        Assert.True(RemoteGeoParquetLocations.TryLocate(Request("s3://bucket/prefix/"), false, out string? locator, out _));

        using GeoParquetSources sources = new(null, "128MB", 1);

        Assert.Throws<InvalidOperationException>(() => sources.FolderFor(locator!));

        Graticula.Host.SourceUnreachableException remembered =
            Assert.Throws<Graticula.Host.SourceUnreachableException>(() => sources.FolderFor(locator!));
        Assert.Contains("Graticula:DuckDbExtensions", remembered.Message, StringComparison.Ordinal);
    }
}
