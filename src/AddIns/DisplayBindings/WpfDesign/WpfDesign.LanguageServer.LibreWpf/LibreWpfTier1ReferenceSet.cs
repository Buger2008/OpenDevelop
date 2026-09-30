using XamlToCSharpGenerator.LanguageService.Workspace.Tier1;

namespace ICSharpCode.WpfDesign.LanguageServer.LibreWpf;

/// <summary>LibreWPF's Tier-1 assemblies: its own PresentationFramework, PresentationCore,
/// WindowsBase and System.Xaml, from the librewpf.transport package every LibreWPF project restores
/// (the payload LibreWPF.Sdk builds and runs against) - never Microsoft's WindowsDesktop reference
/// pack, which is what Microsoft WPF's server reads.</summary>
internal sealed class LibreWpfTier1ReferenceSet : NuGetPackagesTier1ReferenceSet
{
    public static LibreWpfTier1ReferenceSet Instance { get; } = new();

    LibreWpfTier1ReferenceSet()
        : base("LibreWPF",
            new[] { "System.Windows.Controls.Grid", "System.Windows.Controls.Button" },
            ("librewpf.transport", "PresentationFramework.dll"),
            ("librewpf.transport", "PresentationCore.dll"),
            ("librewpf.transport", "WindowsBase.dll"),
            ("librewpf.transport", "System.Xaml.dll"))
    {
    }
}
