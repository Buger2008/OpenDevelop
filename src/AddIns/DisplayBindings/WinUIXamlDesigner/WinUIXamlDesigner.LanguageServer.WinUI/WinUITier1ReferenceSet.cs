using XamlToCSharpGenerator.LanguageService.Workspace.Tier1;

namespace ICSharpCode.WinUIXamlDesigner.LanguageServer.WinUI;

/// <summary>
/// WinUI's Tier-1 assemblies, from the Windows App SDK in the NuGet cache: Microsoft.WinUI.dll plus
/// the Windows SDK projection its public surface is written against (Windows.Foundation, ...).
/// They are present once any WinUI project on the machine has been restored - which works on every
/// OS; only building one needs Windows. When they are absent there is no Tier 1 and the project's
/// own compilation serves alone: WinUI is never served from another framework's assemblies.
/// <para>
/// Microsoft.WinUI.dll maps no xmlns (the mapping is implicit in WinUI's XAML compiler); the WinUI
/// profile names the Microsoft.UI.Xaml.* namespaces instead, which the type index falls back to.
/// </para>
/// </summary>
internal sealed class WinUITier1ReferenceSet : ITier1ReferenceSet
{
    public static WinUITier1ReferenceSet Instance { get; } = new();

    public string Name => "WinUI";

    public IReadOnlyList<string> AnchorTypes { get; } =
        new[] { "Microsoft.UI.Xaml.Controls.Grid", "Microsoft.UI.Xaml.Controls.Page" };

    public IEnumerable<string> ResolveFrameworkAssemblies(Tier1ReferenceEnvironment environment)
    {
        // Windows App SDK 1.7+ splits WinUI into its own package; earlier releases ship it inside
        // Microsoft.WindowsAppSDK itself.
        var winui = FindAssembly(environment, "Microsoft.WinUI.dll", "microsoft.windowsappsdk.winui", "microsoft.windowsappsdk");
        if (winui is null)
        {
            Console.Error.WriteLine(
                $"[WinUI-LS] No Windows App SDK (Microsoft.WinUI.dll) in the NuGet cache ({environment.NuGetPackagesRoot}); " +
                "WinUI Tier-1 unavailable until a WinUI project is restored.");
            return Array.Empty<string>();
        }

        Console.Error.WriteLine($"[WinUI-LS] WinUI Tier-1 assembly: {winui}");
        var paths = new List<string> { winui };
        foreach (var projection in new[] { "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll" })
        {
            if (FindAssembly(environment, projection, "microsoft.windows.sdk.net.ref") is { } path)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>The assembly from the highest cached version of the first package that has it, in
    /// its highest <c>lib/net*</c> folder. WinUI ships only Windows builds
    /// (<c>net6.0-windows10.0.17763.0</c>); their metadata reads on any OS.</summary>
    static string? FindAssembly(Tier1ReferenceEnvironment environment, string assembly, params string[] packages)
    {
        foreach (var package in packages)
        {
            var version = environment.FindLatestPackageVersion(package, versionDir => FindInLib(versionDir, assembly) is not null);
            if (version is not null)
            {
                return FindInLib(Path.Combine(environment.NuGetPackagesRoot, package, version), assembly);
            }
        }

        return null;
    }

    static string? FindInLib(string versionDir, string assembly)
    {
        var lib = Path.Combine(versionDir, "lib");
        if (!Directory.Exists(lib))
        {
            return null;
        }

        return Directory.GetDirectories(lib)
            .Where(dir => Path.GetFileName(dir).StartsWith("net", StringComparison.OrdinalIgnoreCase)
                          && !Path.GetFileName(dir).StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static d => d, Tier1ReferenceEnvironment.VersionDirComparer)
            .Select(dir => Path.Combine(dir, assembly))
            .FirstOrDefault(File.Exists);
    }
}
