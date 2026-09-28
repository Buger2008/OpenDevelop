namespace ICSharpCode.SharpDevelop.LanguageServices.Xaml;

/// <summary>Maps evaluated project runtime identity to a deployed child host. The result is
/// deliberately a file name only: no UI-framework assembly crosses into the IDE process.</summary>
public static class XamlDesignerHostSelector
{
	/// <summary>
	/// The host that serves <paramref name="context"/>. A dialect registered with
	/// <see cref="XamlDialectRegistry"/> that names a host wins, so an out-of-tree designer
	/// supplies its own child without an edit here; the built-in switch below keeps deciding for
	/// the built-in dialects, where the host legitimately depends on the project's runtime
	/// rather than on the dialect (WPF has a separate LibreWPF and Microsoft WPF host).
	/// </summary>
	public static string? GetHostAssemblyName(XamlFrameworkContext context)
	{
		string? dialect = XamlFrameworkKindNames.For(context.Kind);
		if (dialect != null)
		{
			string? registered = XamlDialectRegistry.GetHostAssemblyName(dialect);
			if (registered != null) return registered;
		}

		return context.Runtime switch {
			XamlRuntimeKind.LibreWpf => "WpfDesign.SurfaceHost.dll",
			XamlRuntimeKind.MicrosoftWpf => "MicrosoftWpfPreview.Host.dll",
			XamlRuntimeKind.Uno => "WinUIXamlDesigner.UnoHost.dll",
			XamlRuntimeKind.MicrosoftWinUI => "WinUIXamlDesigner.MicrosoftHost.dll",
			_ => null
		};
	}
}

static class XamlFrameworkKindNames
{
	/// <summary>The routing key a built-in dialect is known by. Shared with
	/// <see cref="XamlDialectRegistry"/>, so the workbench's filter and a binding's
	/// <c>Dialects</c> declaration cannot drift apart by spelling.</summary>
	internal static string? For(XamlFrameworkKind kind) => kind switch {
		XamlFrameworkKind.Wpf => XamlDialectKeys.Wpf,
		XamlFrameworkKind.WinUI => XamlDialectKeys.WinUI,
		XamlFrameworkKind.Uno => XamlDialectKeys.Uno,
		_ => null
	};
}
