using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Web;

/// <summary>
/// Shape File Transformation hands to <see cref="IndexHtmlTransformation.Transform"/>. It converts
/// <c>{ "contents": "..." }</c> into this type, so it needs a public parameterless constructor and setter.
/// </summary>
public sealed class TransformPayload
{
    /// <summary>Gets or sets the current file text.</summary>
    public string? Contents { get; set; }
}

/// <summary>
/// The <c>index.html</c> transformation: adds our menu script tag before <c>&lt;/body&gt;</c>.
/// Called by File Transformation through reflection, so the class, method and signature are a contract.
/// </summary>
public static class IndexHtmlTransformation
{
    /// <summary>The tag we add. Relative to <c>/web/</c>, so a base URL keeps working.</summary>
    public const string ScriptTag = "<script plugin=\"SubtitleFixer\" src=\"../SubtitleFixer/menu.js\" defer></script>";

    /// <summary>Adds the script tag. Returns the input unchanged when the tag is already there or there is no body.</summary>
    /// <param name="payload">The file contents.</param>
    /// <returns>New file contents.</returns>
    public static string Transform(TransformPayload payload)
    {
        var html = payload?.Contents ?? string.Empty;
        if (html.Contains("plugin=\"SubtitleFixer\"", StringComparison.Ordinal))
        {
            return html;
        }

        var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyEnd < 0 ? html : html.Insert(bodyEnd, ScriptTag);
    }
}

/// <summary>
/// Registers <see cref="IndexHtmlTransformation"/> with the File Transformation plugin by reflection
/// (M0-SPIKES.md S1). Builds the Newtonsoft <c>JObject</c> from File Transformation's own parameter type so we
/// never ship Newtonsoft (D16).
/// </summary>
public sealed class FileTransformationRegistrar
{
    /// <summary>Our transformation id. Fixed so re-registration replaces instead of duplicating.</summary>
    public static readonly Guid TransformationId = Guid.Parse("662318bb-df8a-4590-a260-6195bd519add");

    private const string PluginInterfaceType = "Jellyfin.Plugin.FileTransformation.PluginInterface";

    private readonly ILogger<FileTransformationRegistrar> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransformationRegistrar"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public FileTransformationRegistrar(ILogger<FileTransformationRegistrar> logger)
    {
        _logger = logger;
    }

    /// <summary>Builds the registration payload JSON.</summary>
    /// <returns>JSON text.</returns>
    public static string BuildPayloadJson()
    {
        var callbackType = typeof(IndexHtmlTransformation);
        return JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["id"] = TransformationId.ToString(),
            ["fileNamePattern"] = "index.html",
            ["callbackAssembly"] = callbackType.Assembly.FullName,
            ["callbackClass"] = callbackType.FullName,
            ["callbackMethod"] = nameof(IndexHtmlTransformation.Transform),
        });
    }

    /// <summary>Tries to register once.</summary>
    /// <param name="error">Why it failed.</param>
    /// <returns>True when registered.</returns>
    public bool TryRegister(out string? error)
    {
        try
        {
            var assembly = AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
            if (assembly is null)
            {
                error = "The File Transformation plugin is not installed or not loaded.";
                return false;
            }

            var register = assembly.GetType(PluginInterfaceType)?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            var payloadType = register?.GetParameters() is [{ } only] ? only.ParameterType : null;
            var parse = payloadType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
            if (register is null || parse is null)
            {
                error = "This File Transformation version has no compatible RegisterTransformation(JObject).";
                return false;
            }

            var payload = parse.Invoke(null, [BuildPayloadJson()]);
            register.Invoke(null, [payload]);
            _logger.LogInformation("Subtitle Fixer registered its index.html transformation with File Transformation");
            error = null;
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            error = ex.InnerException.Message;
            _logger.LogWarning(ex.InnerException, "File Transformation registration failed");
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or MemberAccessException or NotSupportedException)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "File Transformation registration failed");
            return false;
        }
    }
}
