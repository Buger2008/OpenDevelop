using ICSharpCode.WinUIXamlDesigner.LanguageServer.WinUI;
using XamlToCSharpGenerator.LanguageServer.Hosting;
using XamlToCSharpGenerator.LanguageService.Framework.WinUI;

// The WinUI XAML language server: WinUI only. Uno, although it implements the same API, has its own
// server (uno-xaml-ls); this one never serves another framework's XAML, and its Tier 1 comes only
// from the Windows App SDK.
Environment.ExitCode = await XamlLanguageServerHost.RunAsync(
    args,
    WinUiLanguageFrameworkProvider.Instance.Framework,
    WinUITier1ReferenceSet.Instance);
