using ICSharpCode.WpfDesign.LanguageServer.LibreWpf;
using XamlToCSharpGenerator.LanguageServer.Hosting;
using XamlToCSharpGenerator.LanguageService.Framework.Wpf;

// The LibreWPF XAML language server: LibreWPF only. Its markup is WPF's (hence the WPF language
// profile), but its assemblies are LibreWPF's own, so it has its own server rather than Microsoft
// WPF's, and its Tier 1 comes only from the LibreWPF packages.
Environment.ExitCode = await XamlLanguageServerHost.RunAsync(
    args,
    WpfLanguageFrameworkProvider.Instance.Framework,
    LibreWpfTier1ReferenceSet.Instance);
