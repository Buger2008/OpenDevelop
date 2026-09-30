using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.XamlBinding
{
	/// <summary>
	/// The single ".xaml" binding on <see cref="LanguageServiceRegistry"/>. It routes each file to the
	/// language server of the file's XAML runtime, as registered with <see cref="XamlLanguageServers"/>
	/// by the addin that owns that runtime: the WPF designer registers Microsoft WPF's and LibreWPF's,
	/// the WinUI designer Microsoft WinUI's, ProGPU WinUI's and Uno's, the MAUI addin MAUI's. The
	/// registry holds one resolver per extension, so the servers cannot each register ".xaml"
	/// themselves - they would replace one another. A file whose runtime has no server gets no
	/// language service (lexical highlighting only) rather than another runtime's.
	/// </summary>
	public sealed class RegisterXamlLanguageServiceCommand : AbstractCommand, IDisposable
	{
		IDisposable registration;

		public override void Run()
		{
			registration = SD.GetRequiredService<LanguageServiceRegistry>()
				.RegisterExtension(".xaml", Resolve);
		}

		static LspLanguageService Resolve(string fileName)
		{
			var spec = XamlLanguageServers.GetLaunchSpec(fileName);
			return spec == null ? null : LspServiceManager.GetService(fileName, spec);
		}

		public void Dispose() => registration?.Dispose();
	}
}
