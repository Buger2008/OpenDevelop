using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.WinUIXamlDesigner
{
	/// <summary>
	/// Registers this addin's XAML language servers, one per runtime it designs: winui-xaml-ls for
	/// Microsoft WinUI, progpu-winui-xaml-ls for ProGPU WinUI and uno-xaml-ls for Uno. All three are
	/// WinUI markup, but each runtime implements Microsoft.UI.Xaml with its own assemblies, so each
	/// has its own server reading only those. XamlBinding's ".xaml" resolver routes a file to the
	/// server registered for its runtime (see <see cref="XamlLanguageServers"/>).
	/// </summary>
	public sealed class RegisterXamlLanguageServersCommand : AbstractCommand, IDisposable
	{
		IDisposable winui;
		IDisposable progpu;
		IDisposable uno;

		public override void Run()
		{
			winui = Register(XamlLanguageServers.MicrosoftWinUI, "WinUIXamlLanguageServer", "winui-xaml-ls.dll");
			progpu = Register(XamlLanguageServers.ProGpuWinUI, "ProGpuWinUIXamlLanguageServer", "progpu-winui-xaml-ls.dll");
			uno = Register(XamlLanguageServers.Uno, "UnoXamlLanguageServer", "uno-xaml-ls.dll");
			// The XAML source editor's Toolbox for WinUI/Uno files (see XamlDialectRegistry).
			XamlDialectRegistry.RegisterBuiltInToolsContent(XamlDialectKeys.WinUI, () => WinUIXamlToolbox.Instance.ToolboxControl);
			XamlDialectRegistry.RegisterBuiltInToolsContent(XamlDialectKeys.Uno, () => WinUIXamlToolbox.Instance.ToolboxControl);
		}

		static IDisposable Register(string key, string folder, string assembly)
		{
			var server = XamlLanguageServers.FindDeployedServer(folder, assembly);
			if (server == null)
			{
				LoggingService.Warn($"WinUIXamlDesigner: {assembly} is not deployed (AddIns/LanguageServices/{folder}); {key} XAML has no language service.");
				return null;
			}
			return XamlLanguageServers.Register(key, server);
		}

		public void Dispose()
		{
			winui?.Dispose();
			progpu?.Dispose();
			uno?.Dispose();
		}
	}
}
