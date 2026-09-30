using ICSharpCode.WinUIXamlDesigner.LanguageServer.ProGpu;
using XamlToCSharpGenerator.LanguageServer.Hosting;
using XamlToCSharpGenerator.LanguageService.Framework.WinUI;

// The ProGPU WinUI XAML language server: ProGPU WinUI only. Its markup is WinUI's (hence the WinUI
// language profile), but its controls are ProGPU's own implementation of Microsoft.UI.Xaml, so it
// has its own server rather than Microsoft WinUI's or Uno's, and its Tier 1 comes only from the
// ProGPU packages.
Environment.ExitCode = await XamlLanguageServerHost.RunAsync(
    args,
    WinUiLanguageFrameworkProvider.Instance.Framework,
    ProGpuWinUITier1ReferenceSet.Instance);
