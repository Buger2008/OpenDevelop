using XamlToCSharpGenerator.LanguageService.Workspace.Tier1;

namespace ICSharpCode.WinUIXamlDesigner.LanguageServer.ProGpu;

/// <summary>ProGPU WinUI's Tier-1 assemblies from the NuGet cache: ProGPU.WinUI (its
/// Microsoft.UI.Xaml.* controls) and ProGPU.WinRT (the Windows.* types they expose). The ProGPU
/// packages are versioned independently, so each is read at its own latest cached version. Like
/// Microsoft WinUI's, ProGPU.WinUI.dll maps no xmlns; the WinUI profile's namespace list covers it.</summary>
internal sealed class ProGpuWinUITier1ReferenceSet : NuGetPackagesTier1ReferenceSet
{
    public static ProGpuWinUITier1ReferenceSet Instance { get; } = new();

    ProGpuWinUITier1ReferenceSet()
        : base("ProGPU.WinUI",
            new[] { "Microsoft.UI.Xaml.Controls.Grid", "Microsoft.UI.Xaml.Controls.Page" },
            IsPlatformNeutral,
            oneRelease: false,
            ("progpu.winui", "ProGPU.WinUI.dll"),
            ("progpu.winrt", "ProGPU.WinRT.dll"))
    {
    }
}
