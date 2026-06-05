using System.IO;
using System.Reflection;
using System.Text.Json;

namespace FileNinja.FlowLauncher.Tests;

/// <summary>
/// Deployment-shape tests. These would have caught the original hang: Flow.Launcher
/// loads its own copy of Flow.Launcher.Plugin.dll, and if the plugin folder ships
/// a second copy, the host loads two distinct IAsyncPlugin types and either hangs
/// at type resolution or refuses the plugin silently.
/// </summary>
public class PluginPackagingTests
{
    /// <summary>
    /// Locates the plugin's own build output folder by walking up from the test
    /// assembly. Returns null when neither Debug nor Release output exists — used
    /// to skip these tests on a fresh checkout where the plugin hasn't been built.
    /// </summary>
    private static string? FindPluginOutputDir()
    {
        var testDir = Path.GetDirectoryName(typeof(PluginPackagingTests).Assembly.Location)!;
        // tests/bin/<cfg>/<tfm> → walk back to flowlauncher-plugin/
        var pluginRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", ".."));

        foreach (var cfg in new[] { "Release", "Debug" })
        {
            // The plugin csproj uses AppendTargetFrameworkToOutputPath=false to
            // flatten output. Also probe the TFM subfolder as a fallback so older
            // build artifacts still get found.
            foreach (var leaf in new[] { "", "net7.0-windows", "net8.0-windows" })
            {
                var candidate = Path.Combine(pluginRoot, "bin", cfg, leaf);
                if (File.Exists(Path.Combine(candidate, "plugin.json")))
                    return candidate;
            }
        }
        return null;
    }

    [Fact]
    public void Output_IsFlat_NotNestedUnderTfmFolder()
    {
        var dir = FindPluginOutputDir();
        dir.Should().NotBeNull();

        // The plugin folder we ship must contain plugin.json directly. If we
        // accidentally remove AppendTargetFrameworkToOutputPath=false, output
        // ends up at bin/<cfg>/net7.0-windows/ and the user installs a folder
        // shaped wrong for Flow.Launcher's installer.
        Path.GetFileName(dir).Should().NotMatch("net?.0*",
            "plugin output must be flat (bin/<cfg>/), not nested under a TFM folder.");
    }

    [Fact]
    public void Output_DoesNotContain_BuildOnlyTransientDeps()
    {
        var dir = FindPluginOutputDir();
        dir.Should().NotBeNull("the plugin must be built before running packaging tests; the ProjectReference normally guarantees this.");

        // JetBrains.Annotations is a build-time annotation lib pulled in transitively
        // by Flow.Launcher.Plugin. It has no business in the runtime folder.
        File.Exists(Path.Combine(dir!, "JetBrains.Annotations.dll")).Should().BeFalse();
    }

    [Fact]
    public void PluginJson_HasRequiredFields()
    {
        var dir = FindPluginOutputDir();
        dir.Should().NotBeNull("the plugin must be built before running packaging tests; the ProjectReference normally guarantees this.");

        var json = File.ReadAllText(Path.Combine(dir!, "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var field in new[] { "ID", "ActionKeyword", "Name", "Description", "Author", "Version", "Language", "ExecuteFileName" })
        {
            root.TryGetProperty(field, out var v).Should().BeTrue($"plugin.json must declare '{field}'");
            v.GetString().Should().NotBeNullOrWhiteSpace($"plugin.json field '{field}' must not be empty");
        }

        root.GetProperty("Language").GetString().Should().Be("csharp");
    }

    [Fact]
    public void PluginJson_ID_IsValidGuidFormat()
    {
        var dir = FindPluginOutputDir();
        dir.Should().NotBeNull("the plugin must be built before running packaging tests; the ProjectReference normally guarantees this.");

        var json = File.ReadAllText(Path.Combine(dir!, "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("ID").GetString();

        id.Should().HaveLength(32, "Flow.Launcher expects a 32-char hex GUID-shape identifier");
        id.Should().MatchRegex("^[A-Fa-f0-9]{32}$");
    }

    [Fact]
    public void PluginJson_IcoPath_ResolvesToAFile()
    {
        var dir = FindPluginOutputDir();
        dir.Should().NotBeNull("the plugin must be built before running packaging tests; the ProjectReference normally guarantees this.");

        var json = File.ReadAllText(Path.Combine(dir!, "plugin.json"));
        using var doc = JsonDocument.Parse(json);

        // IcoPath is optional in the schema but, if declared, must resolve. Flow.Launcher
        // is usually forgiving and falls back to a default icon, but some Flow versions
        // log noise and a missing icon also reads as "this plugin looks unfinished".
        if (doc.RootElement.TryGetProperty("IcoPath", out var ico))
        {
            var iconPath = ico.GetString();
            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                var absolute = Path.IsPathRooted(iconPath) ? iconPath : Path.Combine(dir!, iconPath);
                File.Exists(absolute).Should().BeTrue(
                    $"plugin.json declares IcoPath='{iconPath}' but no such file exists in the plugin folder.");
            }
        }
    }

    [Fact]
    public void PluginJson_ExecuteFileName_MatchesAnActualDll()
    {
        var dir = FindPluginOutputDir();
        dir.Should().NotBeNull("the plugin must be built before running packaging tests; the ProjectReference normally guarantees this.");

        var json = File.ReadAllText(Path.Combine(dir!, "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        var execFile = doc.RootElement.GetProperty("ExecuteFileName").GetString()!;

        File.Exists(Path.Combine(dir!, execFile)).Should().BeTrue(
            $"plugin.json points at '{execFile}' but no such file exists in the plugin folder — Flow.Launcher will refuse to load the plugin.");
    }

    [Fact]
    public void PluginDll_ExposesIAsyncPluginImplementation()
    {
        var dir = FindPluginOutputDir();
        dir.Should().NotBeNull("the plugin must be built before running packaging tests; the ProjectReference normally guarantees this.");

        var json = File.ReadAllText(Path.Combine(dir!, "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        var execFile = doc.RootElement.GetProperty("ExecuteFileName").GetString()!;

        var asm = Assembly.LoadFrom(Path.Combine(dir!, execFile));
        // Use the SDK assembly the test project compiled against — Flow loads
        // its own copy at runtime, but the contract type is identical.
        var iface = typeof(Flow.Launcher.Plugin.IAsyncPlugin);
        var impl = asm.GetExportedTypes().FirstOrDefault(t => iface.IsAssignableFrom(t) && !t.IsAbstract);
        impl.Should().NotBeNull("Flow.Launcher needs a concrete IAsyncPlugin implementation to instantiate.");
    }
}
