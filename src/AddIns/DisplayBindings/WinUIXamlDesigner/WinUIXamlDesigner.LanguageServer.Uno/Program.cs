using ICSharpCode.WinUIXamlDesigner.LanguageServer.Uno;
using XamlToCSharpGenerator.LanguageServer.Hosting;
using XamlToCSharpGenerator.LanguageService.Framework.Uno;

// The Uno Platform XAML language server: Uno only. WinUI, although Uno implements its API, has its
// own server (winui-xaml-ls); this one never serves another framework's XAML, and its Tier 1 comes
// only from the Uno packages.
Environment.ExitCode = await XamlLanguageServerHost.RunAsync(
    args,
    UnoLanguageFrameworkProvider.Instance.Framework,
    UnoTier1ReferenceSet.Instance);
