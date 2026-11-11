using System.Text.Json;
using Jellyfin.Plugin.SubtitleFixer.Web;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

public sealed class WebTests
{
    [Fact]
    public void Script_tag_goes_before_the_closing_body_once()
    {
        var html = "<html><body><div></div></body></html>";

        var once = IndexHtmlTransformation.Transform(new TransformPayload { Contents = html });
        var twice = IndexHtmlTransformation.Transform(new TransformPayload { Contents = once });

        Assert.Equal("<html><body><div></div>" + IndexHtmlTransformation.ScriptTag + "</body></html>", once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Html_without_body_is_unchanged()
    {
        Assert.Equal("<p>x</p>", IndexHtmlTransformation.Transform(new TransformPayload { Contents = "<p>x</p>" }));
        Assert.Equal(string.Empty, IndexHtmlTransformation.Transform(new TransformPayload()));
    }

    [Fact]
    public void Registration_payload_points_at_our_callback()
    {
        using var doc = JsonDocument.Parse(FileTransformationRegistrar.BuildPayloadJson());
        var root = doc.RootElement;

        Assert.Equal(FileTransformationRegistrar.TransformationId.ToString(), root.GetProperty("id").GetString());
        Assert.Equal("index.html", root.GetProperty("fileNamePattern").GetString());
        Assert.Equal(typeof(IndexHtmlTransformation).Assembly.FullName, root.GetProperty("callbackAssembly").GetString());

        var type = typeof(IndexHtmlTransformation).Assembly.GetType(root.GetProperty("callbackClass").GetString()!);
        var method = type!.GetMethod(root.GetProperty("callbackMethod").GetString()!);
        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.Equal(typeof(string), method.ReturnType);
        Assert.Single(method.GetParameters());
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("app.js")]
    [InlineData("styles.css")]
    [InlineData("menu.js")]
    public void Web_files_are_embedded(string name)
    {
        var assembly = typeof(Plugin).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Web.{name}");
        Assert.NotNull(stream);
    }

    [Fact]
    public void Config_page_is_embedded()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Configuration.configPage.html");
        Assert.NotNull(stream);
    }
}
