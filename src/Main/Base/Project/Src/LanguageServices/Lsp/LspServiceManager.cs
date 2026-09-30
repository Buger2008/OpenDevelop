using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.ILSpy.Util;

namespace ICSharpCode.SharpDevelop.LanguageServices.Lsp
{
	public static class LspServiceManager
	{
		static readonly LspServerRegistry registry = LspServerRegistry.CreateDefault();
		static readonly Dictionary<string, LspLanguageService> services = new(StringComparer.OrdinalIgnoreCase);
		static readonly Dictionary<string, string> rootByKey = new(StringComparer.OrdinalIgnoreCase);
		static readonly object syncRoot = new();
		static int lifecycleHooked;

		/// <summary>
		/// One server runs per workspace root and nothing else ever stopped one: closing a document
		/// or a solution left it running for the rest of the IDE's life, so every project opened in a
		/// session added a process (21 XAML servers were measured in one integration run, one per
		/// test workspace). A server now ends with the solution its workspace belongs to; a file
		/// that is still open and asks again simply starts a fresh one.
		/// </summary>
		static void EnsureLifecycleHooked()
		{
			if (Interlocked.Exchange(ref lifecycleHooked, 1) != 0)
				return;
			MessageBus<SolutionClosedMessageEventArgs>.Subscribers += (_, e) => ReleaseServicesFor(e.Solution);
		}

		static void ReleaseServicesFor(ISolution solution)
		{
			var directories = new List<string>();
			if (solution.Directory is { } solutionDirectory)
				directories.Add(solutionDirectory.ToString());
			foreach (var project in solution.Projects)
				if (project.Directory is { } projectDirectory)
					directories.Add(projectDirectory.ToString());
			ReleaseServicesUnder(directories);
		}

		/// <summary>Stops every server whose workspace root is one of <paramref name="directories"/>
		/// or lies below one. Returns how many were released.</summary>
		public static int ReleaseServicesUnder(IEnumerable<string> directories)
		{
			var roots = directories.Where(d => !string.IsNullOrEmpty(d)).Select(Normalize).ToArray();
			List<LspLanguageService> released;
			lock (syncRoot) {
				var keys = rootByKey.Where(p => roots.Any(root => IsUnder(Normalize(p.Value), root))).Select(p => p.Key).ToList();
				released = keys.Select(key => services[key]).ToList();
				foreach (var key in keys) {
					services.Remove(key);
					rootByKey.Remove(key);
				}
			}
			foreach (var service in released) {
				// Off the caller's thread: solution close runs on the UI thread, and the LSP
				// shutdown handshake is a round-trip to the server.
				Task.Run(async () => {
					try {
						await service.DisposeAsync();
					} catch (Exception ex) {
						LoggingService.Warn("LspServiceManager: stopping a language server failed: " + ex.Message);
					}
				});
			}
			if (released.Count > 0)
				LoggingService.Info($"LspServiceManager: released {released.Count} language server(s) for closed workspace(s)");
			return released.Count;
		}

		/// <summary>How many language servers are currently held (running or startable).</summary>
		public static int ServiceCount { get { lock (syncRoot) return services.Count; } }

		static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

		static bool IsUnder(string path, string root) =>
			string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Allows addins to register additional LSP server mappings at startup.
		/// Called by addin startup commands, not from the Base project.
		/// </summary>
		public static void RegisterExtension(string extension, LspServerLaunchSpec spec)
		{
			if (spec is null)
				throw new ArgumentNullException(nameof(spec));
			lock (syncRoot) {
				registry.Register(extension, spec);
			}
		}

		public static LspLanguageService GetService(string fileName)
		{
			var extension = Path.GetExtension(fileName);
			LspServerLaunchSpec spec;
			lock (syncRoot) {
				if (!registry.TryGetLaunchSpec(extension, out spec)) {
					LoggingService.Debug($"LspServiceManager: no launch spec for extension '{extension}' ({fileName})");
					return null;
				}
			}

			return GetService(fileName, spec);
		}

		/// <summary>
		/// The service for <paramref name="fileName"/> started from <paramref name="spec"/>, for a
		/// caller whose launch depends on the file rather than only its extension (the XAML server
		/// is started per framework and per project). One server runs per distinct launch - language,
		/// command line and workspace root - so two specs that differ only in arguments never share
		/// a process.
		/// </summary>
		public static LspLanguageService GetService(string fileName, LspServerLaunchSpec spec)
		{
			if (spec is null)
				throw new ArgumentNullException(nameof(spec));
			EnsureLifecycleHooked();
			var rootPath = FindWorkspaceRoot(fileName);
			var key = spec.LanguageId + "\0" + spec.Command + "\0" + string.Join("\0", spec.Arguments) + "\0" + rootPath;
			lock (syncRoot) {
				if (!services.TryGetValue(key, out var service)) {
					var rootUri = new Uri(rootPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
						? rootPath
						: rootPath + Path.DirectorySeparatorChar).AbsoluteUri;
					// One line per server started, naming the file that caused it: which process
					// answers a document is otherwise invisible, and two specs for one extension
					// (the XAML server runs per framework) are easy to confuse.
					LoggingService.Info($"LspServiceManager: starting '{spec.LanguageId}' server for '{fileName}': {spec.Command} {string.Join(" ", spec.Arguments)} (root {rootPath})");
					service = new LspLanguageService(spec, rootUri);
					services[key] = service;
					rootByKey[key] = rootPath;
				}
				return service;
			}
		}

		static string FindWorkspaceRoot(string fileName)
		{
			var requestedDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory;
			var directory = new DirectoryInfo(requestedDirectory);
			while (directory != null) {
				if (ContainsWorkspaceFile(directory.FullName))
					return directory.FullName;
				directory = directory.Parent;
			}
			return requestedDirectory;
		}

		/// <summary>
		/// A solution (.sln/.slnx/.slnf) or MSBuild project (.csproj, .vbproj, .fsproj, ...) file.
		/// Not the "*.sln*"/"*.*proj" wildcards this used to be: .NET matches those case-insensitively
		/// on macOS and Windows, so any file whose name merely ENDS in "proj" counted - measured, a
		/// hidden temp file ".com.openai.codex.L2pRoJ" in $TMPDIR made the whole temp folder a
		/// "workspace", and every temporary project's server was rooted there.
		/// </summary>
		static bool IsWorkspaceFile(string path)
		{
			var name = Path.GetFileName(path);
			if (name.StartsWith(".", StringComparison.Ordinal))
				return false;
			var extension = Path.GetExtension(name);
			if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
			    || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
			    || extension.Equals(".slnf", StringComparison.OrdinalIgnoreCase))
				return true;
			return extension.Length > ".proj".Length
			       && extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
			       && extension.Skip(1).All(char.IsAsciiLetter);
		}

		static bool ContainsWorkspaceFile(string directory)
		{
			// Editor hover and other delayed UI work may run after a test/project has deleted its
			// temporary workspace. Directory.Exists alone cannot close the check/enumerate race,
			// so enumeration itself must tolerate the directory (or a mounted ancestor) vanishing.
			try {
				return Directory.EnumerateFiles(directory).Any(IsWorkspaceFile);
			} catch (DirectoryNotFoundException) {
				return false;
			} catch (IOException) {
				return false;
			} catch (UnauthorizedAccessException) {
				return false;
			}
		}
	}
}
