using System.Net;
using System.Text;
using Jellyfin.Plugin.SubtitleFixer.Api;
using Jellyfin.Plugin.SubtitleFixer.Bazarr;
using Jellyfin.Plugin.SubtitleFixer.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

public sealed class BazarrPathMapperTests
{
    [Fact]
    public void Parse_reads_lines_and_reports_bad_ones_by_number()
    {
        var r = BazarrPathMapper.Parse("D:\\TV => /tv\r\n\r\n# comment\nno arrow here\n => /x\na => b => c\n");

        Assert.Equal([new BazarrPathMapping("D:\\TV", "/tv")], r.Mappings);
        Assert.Equal(3, r.Errors.Count);
        Assert.StartsWith("Line 4:", r.Errors[0], StringComparison.Ordinal);
        Assert.StartsWith("Line 5:", r.Errors[1], StringComparison.Ordinal);
        Assert.StartsWith("Line 6:", r.Errors[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Empty_setting_has_no_mappings_and_no_errors(string? text)
    {
        var r = BazarrPathMapper.Parse(text);
        Assert.Empty(r.Mappings);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Maps_windows_prefix_to_linux_prefix_ignoring_case()
    {
        var mapper = BazarrPathMapper.FromSetting(@"D:\TV => /data/tv");

        Assert.Equal("/data/tv/Show Ü/Season 1/E01.mkv", mapper.ToBazarr(@"d:\tv\Show Ü\Season 1\E01.mkv"));
    }

    [Fact]
    public void Matches_whole_folder_names_only()
    {
        var mapper = BazarrPathMapper.FromSetting(@"D:\TV => /tv");

        Assert.Equal(@"D:\TV Shows\a.mkv", mapper.ToBazarr(@"D:\TV Shows\a.mkv"));
    }

    [Fact]
    public void Longest_prefix_wins_whatever_the_order()
    {
        var mapper = BazarrPathMapper.FromSetting("D:\\Media => /media\nD:\\Media\\Anime => /anime");

        Assert.Equal("/anime/x.mkv", mapper.ToBazarr(@"D:\Media\Anime\x.mkv"));
        Assert.Equal("/media/TV/x.mkv", mapper.ToBazarr(@"D:\Media\TV\x.mkv"));
    }

    [Fact]
    public void Windows_target_keeps_backslashes_and_trailing_separators_are_ignored()
    {
        var mapper = BazarrPathMapper.FromSetting(@"\\nas\tv\ => T:\");

        Assert.Equal(@"T:\Show\a.mkv", mapper.ToBazarr(@"\\NAS\tv\Show\a.mkv"));
    }

    [Fact]
    public void Root_prefix_maps_everything_below_it()
    {
        var mapper = BazarrPathMapper.FromSetting("/ => /mnt/");

        Assert.Equal("/mnt/tv/a.mkv", mapper.ToBazarr("/tv/a.mkv"));
    }

    [Fact]
    public void Unmapped_paths_come_back_unchanged()
    {
        Assert.Equal(@"E:\x.mkv", BazarrPathMapper.FromSetting(@"D:\TV => /tv").ToBazarr(@"E:\x.mkv"));
        Assert.Equal(@"E:\x.mkv", BazarrPathMapper.FromSetting(null).ToBazarr(@"E:\x.mkv"));
    }

    [Fact]
    public void SamePath_ignores_case_and_separator_style()
    {
        Assert.True(BazarrPathMapper.SamePath(@"D:\TV\a.mkv", "d:/tv/A.MKV"));
        Assert.False(BazarrPathMapper.SamePath(@"D:\TV\a.mkv", @"D:\TV\b.mkv"));
        Assert.False(BazarrPathMapper.SamePath(null, "x"));
    }
}

public sealed class BazarrConnectionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("localhost:6767")]
    [InlineData("ftp://host")]
    [InlineData("http://host/?apikey=x")]
    [InlineData("http://user:pass@host")]
    public void Rejects_bad_addresses(string url)
    {
        Assert.False(BazarrConnection.TryCreate(url, "key", out var c, out var error));
        Assert.Null(c);
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejects_missing_key()
    {
        Assert.False(BazarrConnection.TryCreate("http://host:6767", " ", out _, out var error));
        Assert.Contains("API key", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://host:6767", "http://host:6767/")]
    [InlineData(" http://host:6767/bazarr ", "http://host:6767/bazarr/")]
    [InlineData("https://host/bazarr/#x", "https://host/bazarr/")]
    public void Normalizes_base_address_to_end_in_slash(string url, string expected)
    {
        Assert.True(BazarrConnection.TryCreate(url, "key", out var c, out _));
        Assert.Equal(expected, c!.BaseUri.ToString());
    }

    [Fact]
    public void ToString_never_contains_the_key()
    {
        BazarrConnection.TryCreate("http://host", "secret-key-123", out var c, out _);
        Assert.DoesNotContain("secret-key-123", c!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key-123", $"{c}", StringComparison.Ordinal);
    }
}

public sealed class BazarrClientTests
{
    private const string Key = "secret-key-123";

    [Fact]
    public async Task Status_sends_key_header_to_api_under_base_path_and_reads_version()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"data":{"bazarr_version":"1.5.3","python_version":"3.12"}}"""));
        var result = await Client(handler).GetStatusAsync(Connection("http://host:6767/bazarr"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("1.5.3", result.Version);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://host:6767/bazarr/api/system/status", request.RequestUri!.ToString());
        Assert.Equal(Key, Assert.Single(request.Headers.GetValues(BazarrClient.ApiKeyHeader)));
        Assert.DoesNotContain(Key, request.RequestUri.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Rejected_key_is_reported(HttpStatusCode status)
    {
        var result = await Client(new StubHandler(_ => Json(status, "{}"))).GetStatusAsync(Connection(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("rejected the API key", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "<html>not bazarr</html>")]
    [InlineData(HttpStatusCode.OK, """{"data":{}}""")]
    [InlineData(HttpStatusCode.OK, "[]")]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    public async Task Something_else_answering_is_reported(HttpStatusCode status, string body)
    {
        var result = await Client(new StubHandler(_ => Json(status, body))).GetStatusAsync(Connection(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Null(result.Version);
        Assert.Contains("address", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreachable_bazarr_is_reported_without_the_key()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("No connection could be made " + Key));
        var result = await Client(handler).GetStatusAsync(Connection(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Could not reach Bazarr", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).GetStatusAsync(Connection(), cts.Token));
    }

    private static BazarrClient Client(StubHandler handler) => new(new StubFactory(handler), NullLogger<BazarrClient>.Instance);

    private static BazarrConnection Connection(string url = "http://host:6767")
    {
        Assert.True(BazarrConnection.TryCreate(url, Key, out var c, out _));
        return c!;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public sealed class BazarrControllerTests
{
    private const string Key = "secret-key-123";

    [Fact]
    public async Task Test_response_never_contains_the_key()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":{"bazarr_version":"1.5.3"}}""", Encoding.UTF8, "application/json"),
        });
        var controller = new BazarrController(new BazarrClient(new StubFactory(handler), NullLogger<BazarrClient>.Instance));

        var result = await controller.TestBazarr(new TestBazarrRequest("http://host:6767", Key, "bad line"), CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result).Content!;
        Assert.Contains("\"ok\":true", content, StringComparison.Ordinal);
        Assert.Contains("\"version\":\"1.5.3\"", content, StringComparison.Ordinal);
        Assert.Contains("Line 1:", content, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_without_address_explains_and_calls_nothing()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var controller = new BazarrController(new BazarrClient(new StubFactory(handler), NullLogger<BazarrClient>.Instance));

        var result = await controller.TestBazarr(new TestBazarrRequest(null, Key, null), CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result).Content!;
        Assert.Contains("\"ok\":false", content, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Bazarr_is_off_by_default()
    {
        var config = new PluginConfiguration();
        Assert.False(config.BazarrEnabled);
        Assert.Equal(string.Empty, config.BazarrUrl);
        Assert.Equal(string.Empty, config.BazarrApiKey);
        Assert.Equal(string.Empty, config.BazarrPathMappings);
    }
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }
}

internal sealed class StubFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

public sealed class BazarrTestTargetTests
{
    private const string SavedUrl = "http://bazarr:6767";
    private const string SavedKey = "saved-key";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://bazarr:6767")]
    [InlineData("HTTP://Bazarr:6767/")]
    public void Saved_key_is_used_for_the_saved_address(string? url)
    {
        var error = BazarrController.ResolveTestConnection(new TestBazarrRequest(url, null, null), SavedUrl, SavedKey, out var c);

        Assert.Null(error);
        Assert.Equal(SavedKey, c!.ApiKey);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://bazarr:6768")]
    [InlineData("https://bazarr:6767")]
    [InlineData("http://bazarr:6767/other")]
    public void Saved_key_is_never_sent_to_another_address(string url)
    {
        var error = BazarrController.ResolveTestConnection(new TestBazarrRequest(url, " ", null), SavedUrl, SavedKey, out var c);

        Assert.NotNull(error);
        Assert.Contains("API key", error, StringComparison.Ordinal);
        Assert.Null(c);
    }

    [Fact]
    public void Typed_key_may_go_to_a_typed_address()
    {
        var error = BazarrController.ResolveTestConnection(new TestBazarrRequest("http://other:6767", "typed", null), SavedUrl, SavedKey, out var c);

        Assert.Null(error);
        Assert.Equal("typed", c!.ApiKey);
        Assert.Equal("http://other:6767/", c.BaseUri.ToString());
    }

    [Fact]
    public void Without_a_saved_key_a_missing_key_is_reported()
    {
        var error = BazarrController.ResolveTestConnection(new TestBazarrRequest("http://other:6767", null, null), SavedUrl, null, out _);

        Assert.Contains("Set the Bazarr API key", error, StringComparison.Ordinal);
    }
}
