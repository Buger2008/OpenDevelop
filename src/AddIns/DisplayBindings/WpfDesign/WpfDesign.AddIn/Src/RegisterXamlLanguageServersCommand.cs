using System;
using System.IO;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// Registers the XAML language servers of the two WPF runtimes this addin designs:
	/// wpf-xaml-ls (externals/vscode-wpf) for Microsoft WPF and librewpf-xaml-ls for LibreWPF. Both
	/// are WPF markup, but they are separate runtimes with separate assemblies, so each has its own
	/// server reading only its own. XamlBinding's ".xaml" resolver routes a file to the server
	/// registered for its runtime (see <see cref="XamlLanguageServers"/>).
	/// </summary>
	public sealed class RegisterXamlLanguageServersCommand : AbstractCommand, IDisposable
	{
		IDisposable microsoftWpf;
		IDisposable libreWpf;

		public override void Run()
		{
			microsoftWpf = Register(XamlLanguageServers.MicrosoftWpf, TryFindMicrosoftWpfServer(), "wpf-xaml-ls.dll");
			libreWpf = Register(XamlLanguageServers.LibreWpf,
				XamlLanguageServers.FindDeployedServer("LibreWpfXamlLanguageServer", "librewpf-xaml-ls.dll"), "librewpf-xaml-ls.dll");
			// The XAML source editor's Toolbox for WPF files (see XamlDialectRegistry).
			XamlDialectRegistry.RegisterBuiltInToolsContent(XamlDialectKeys.Wpf, () => WpfToolbox.Instance.ToolboxControl);
		}

		static IDisposable Register(string key, string server, string assembly)
		{
			if (server == null)
			{
				LoggingService.Warn($"WpfDesign: {assembly} is not deployed; {key} XAML has no language service.");
				return null;
			}
			return XamlLanguageServers.Register(key, server);
		}

		/// <summary>
		/// wpf-xaml-ls.dll: the deployed copy under AddIns/LanguageServices (next to a published
		/// OpenDevelop, or the source tree's, where XamlLanguageServer.Wpf's DeployToAddIns copies
		/// every build whatever its configuration), falling back to that project's own bin output.
		/// The bin fallback prefers Release, which is why it comes last: it once picked a days-old
		/// Release build over the current Debug one.
		/// </summary>
		static string TryFindMicrosoftWpfServer()
		{
			var deployed = XamlLanguageServers.FindDeployedServer("XamlLanguageServer.Wpf", "wpf-xaml-ls.dll");
			if (deployed != null)
				return deployed;

			var binRoot = Path.Combine(FindOpenDevelopRoot(), "externals", "vscode-wpf", "src", "XamlLanguageServer.Wpf", "bin");
			if (!Directory.Exists(binRoot))
				return null;

			return new[] { "Release", "Debug" }
				.Select(configuration => Path.Combine(binRoot, configuration))
				.Where(Directory.Exists)
				.SelectMany(configurationDirectory => Directory.GetFiles(configurationDirectory, "wpf-xaml-ls.dll", SearchOption.AllDirectories))
				.OrderByDescending(File.GetLastWriteTimeUtc)
				.FirstOrDefault();
		}

		static string FindOpenDevelopRoot()
		{
			foreach (var candidate in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
			{
				for (var directory = new DirectoryInfo(candidate); directory != null; directory = directory.Parent)
				{
					if (Directory.Exists(Path.Combine(directory.FullName, "externals", "vscode-wpf")) &&
						Directory.Exists(Path.Combine(directory.FullName, "src", "Main", "Base")))
						return directory.FullName;
				}
			}

			return Environment.CurrentDirectory;
		}

		public void Dispose()
		{
			microsoftWpf?.Dispose();
			libreWpf?.Dispose();
		}
	}
}
