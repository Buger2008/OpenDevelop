using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.LanguageServices.Xaml;

/// <summary>The routing keys of the built-in dialects. Bindings declare these and
/// <see cref="XamlDialectRegistry"/> resolves them from the same constants, so a spelling
/// cannot drift between the two sides and silently stop routing.</summary>
public static class XamlDialectKeys
{
	public const string Wpf = "Wpf";
	public const string WinUI = "WinUI";
	public const string Uno = "Uno";
}

/// <summary>
/// One XAML dialect contributed by a designer, registered at runtime by the addin that owns it.
/// <para>
/// This exists because the built-in designers could coordinate at build time: they live in this
/// repository, so <c>AvalonEdit.AddIn</c> can carry a direct <c>ProjectReference</c> to
/// <c>WpfDesign.AddIn</c> and <c>WinUIXamlDesigner.AddIn</c>, and
/// <see cref="XamlDesignerHostSelector"/> can hard-code which child host serves which runtime.
/// A designer that ships out of tree cannot participate in any of that, so the knowledge that
/// used to be compiled in has to be registered instead - otherwise a new dialect is either
/// unattached, or attached alongside a designer that did not know to decline it.
/// </para>
/// <para>
/// The routing key is deliberately a <see cref="string"/> rather than a new
/// <see cref="XamlFrameworkKind"/> member. The enum stays the closed, compile-time
/// representation for the three built-ins and everything that switches on it stays exhaustive;
/// an out-of-tree dialect never has to be added to it.
/// </para>
/// </summary>
public sealed class XamlDialectRegistration
{
	/// <summary>Stable dialect key, matched against <see cref="IXamlDialectDisplayBinding.Dialects"/>.</summary>
	public string Dialect { get; }

	/// <summary>Decides whether a XAML file belongs to this dialect. Runs before the built-in
	/// project-marker detection, so an out-of-tree dialect is identified on the same evidence
	/// (or better) than a built-in one.</summary>
	public Func<string, bool> Matches { get; }

	/// <summary>File name of the deployed child host that serves this dialect, resolved by
	/// <see cref="XamlDesignerHostSelector"/>. Null when the designer has no separate host.</summary>
	public string? HostAssemblyName { get; }

	public XamlDialectRegistration(string dialect, Func<string, bool> matches, string? hostAssemblyName = null)
	{
		if (string.IsNullOrWhiteSpace(dialect)) throw new ArgumentException("A dialect key is required.", nameof(dialect));
		Dialect = dialect;
		Matches = matches ?? throw new ArgumentNullException(nameof(matches));
		HostAssemblyName = hostAssemblyName;
	}
}

/// <summary>Runtime registry of XAML dialects. See <see cref="XamlDialectRegistration"/>.</summary>
public static class XamlDialectRegistry
{
	static readonly List<XamlDialectRegistration> registrations = new();

	/// <summary>
	/// Registers a dialect. An addin calls this once while it initialises; the workbench and
	/// <see cref="XamlDesignerHostSelector"/> consult it from then on. Registering the same key
	/// twice replaces the earlier entry, so a reload cannot accumulate stale matchers.
	/// </summary>
	public static void Register(XamlDialectRegistration registration)
	{
		if (registration == null) throw new ArgumentNullException(nameof(registration));
		lock (registrations)
		{
			registrations.RemoveAll(existing => string.Equals(existing.Dialect, registration.Dialect, StringComparison.OrdinalIgnoreCase));
			registrations.Add(registration);
		}
	}

	/// <summary>Removes a dialect registration. Used by tests and by addin reload.</summary>
	public static void Unregister(string dialect)
	{
		lock (registrations)
		{
			registrations.RemoveAll(existing => string.Equals(existing.Dialect, dialect, StringComparison.OrdinalIgnoreCase));
		}
	}

	/// <summary>
	/// The dialect that owns <paramref name="xamlFileName"/>, or null when nothing claims it.
	/// <para>
	/// Registered dialects are asked first, then the built-in project-marker detection is
	/// translated to its enum name. Null means "nobody claimed it", which callers must treat as
	/// "do not filter": a WPF file whose project is not in the open solution is deliberately
	/// still designed today, and that behaviour must not change.
	/// </para>
	/// </summary>
	public static string? ResolveDialect(string xamlFileName)
	{
		if (string.IsNullOrEmpty(xamlFileName)) return null;

		XamlDialectRegistration[] snapshot;
		lock (registrations)
		{
			if (registrations.Count == 0) return BuiltInDialect(xamlFileName);
			snapshot = registrations.ToArray();
		}

		foreach (XamlDialectRegistration registration in snapshot)
		{
			try
			{
				if (registration.Matches(xamlFileName)) return registration.Dialect;
			}
			catch (Exception)
			{
				// A third-party matcher must not be able to break detection for every other
				// dialect, let alone for the built-ins.
			}
		}

		return BuiltInDialect(xamlFileName);
	}

	/// <summary>The host assembly a registered dialect asked for, or null.</summary>
	public static string? GetHostAssemblyName(string dialect)
	{
		lock (registrations)
		{
			return registrations.FirstOrDefault(
				registration => string.Equals(registration.Dialect, dialect, StringComparison.OrdinalIgnoreCase)
								&& registration.HostAssemblyName != null)?.HostAssemblyName;
		}
	}

	static string? BuiltInDialect(string xamlFileName)
	{
		try
		{
			return XamlFrameworkDetector.Detect(xamlFileName).Kind switch {
				XamlFrameworkKind.Wpf => XamlDialectKeys.Wpf,
				XamlFrameworkKind.WinUI => XamlDialectKeys.WinUI,
				XamlFrameworkKind.Uno => XamlDialectKeys.Uno,
				_ => null
			};
		}
		catch (Exception)
		{
			return null;
		}
	}
}

/// <summary>
/// Implemented by a secondary display binding that serves a known set of dialects. The workbench
/// uses it to route a file to the binding that owns it, so a binding never has to enumerate the
/// dialects it must decline - which is what made every new dialect cost an edit in every other
/// binding. Bindings that do not implement it keep their current behaviour exactly.
/// </summary>
public interface IXamlDialectDisplayBinding : ISecondaryDisplayBinding
{
	/// <summary>Dialect keys this binding owns, as registered with <see cref="XamlDialectRegistry"/>.</summary>
	IEnumerable<string> Dialects { get; }
}
