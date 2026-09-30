using XamlToCSharpGenerator.LanguageService.Workspace.Tier1;

namespace ICSharpCode.WinUIXamlDesigner.LanguageServer.Uno;

/// <summary>Uno's Tier-1 assemblies: the platform-neutral builds of one Uno release in the NuGet
/// cache. Uno.UI carries Microsoft.UI.Xaml.* and its XmlnsDefinition mapping of the presentation
/// xmlns; Uno.Foundation and Uno (Uno.WinRT) hold the Windows.Foundation and Windows.UI types that
/// surface exposes.</summary>
internal sealed class UnoTier1ReferenceSet : NuGetPackagesTier1ReferenceSet
{
    public static UnoTier1ReferenceSet Instance { get; } = new();

    UnoTier1ReferenceSet()
        : base("Uno",
            new[] { "Microsoft.UI.Xaml.Controls.Grid", "Microsoft.UI.Xaml.Controls.Page" },
            ("uno.winui", "Uno.UI.dll"),
            ("uno.winui", "Uno.Xaml.dll"),
            ("uno.winui", "Uno.UI.Composition.dll"),
            ("uno.foundation", "Uno.Foundation.dll"),
            ("uno.winrt", "Uno.dll"),
            ("uno.winrt", "Uno.UI.Dispatching.dll"))
    {
    }
}
