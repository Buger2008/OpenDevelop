using System.Reflection;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class LspServiceManagerTests
{
	[Fact]
	public void FindWorkspaceRoot_WhenTemporaryWorkspaceWasDeleted_DoesNotThrow()
	{
		var temporaryDirectory = Path.Combine(Path.GetTempPath(), "LspWorkspace-" + Guid.NewGuid().ToString("N"));
		var fileName = Path.Combine(temporaryDirectory, "Page.xaml");
		Directory.CreateDirectory(temporaryDirectory);
		Directory.Delete(temporaryDirectory);

		var method = typeof(LspServiceManager).GetMethod("FindWorkspaceRoot", BindingFlags.Static | BindingFlags.NonPublic);
		Assert.NotNull(method);
		var root = (string)method.Invoke(null, new object[] { fileName })!;

		Assert.Equal(temporaryDirectory, root);
	}

	[Theory]
	[InlineData("App.csproj", true)]
	[InlineData("Lib.fsproj", true)]
	[InlineData("All.sln", true)]
	[InlineData("All.slnx", true)]
	[InlineData(".com.openai.codex.L2pRoJ", false)]
	[InlineData("notes.proj.bak", false)]
	[InlineData("Readme.md", false)]
	public void IsWorkspaceFile_OnlyAcceptsSolutionAndProjectFiles(string fileName, bool expected)
	{
		var method = typeof(LspServiceManager).GetMethod("IsWorkspaceFile", BindingFlags.Static | BindingFlags.NonPublic);
		Assert.NotNull(method);
		Assert.Equal(expected, (bool)method.Invoke(null, new object[] { Path.Combine(Path.GetTempPath(), fileName) })!);
	}

	[Fact]
	public void GetService_WithASpec_ShareOneServerPerLaunch_AndSplitOnArguments()
	{
		// The XAML server is started per framework: a MAUI page and a WPF page in one workspace must
		// never share a process, while two pages of the same framework must.
		var workspace = Path.Combine(Path.GetTempPath(), "LspSpecWorkspace-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(workspace);
		try {
			File.WriteAllText(Path.Combine(workspace, "App.csproj"), "<Project />");
			var first = Path.Combine(workspace, "First.xaml");
			var second = Path.Combine(workspace, "Second.xaml");
			var wpf = new LspServerLaunchSpec("xaml", "dotnet", "exec", "ls.dll", "--workspace", workspace);
			var wpfAgain = new LspServerLaunchSpec("xaml", "dotnet", "exec", "ls.dll", "--workspace", workspace);
			var maui = new LspServerLaunchSpec("xaml", "dotnet", "exec", "ls.dll", "--workspace", workspace, "--framework", "MAUI");

			var a = LspServiceManager.GetService(first, wpf);
			Assert.Same(a, LspServiceManager.GetService(second, wpfAgain));
			Assert.NotSame(a, LspServiceManager.GetService(first, maui));
			Assert.Same(LspServiceManager.GetService(first, maui), LspServiceManager.GetService(second, maui));
		} finally {
			Directory.Delete(workspace, recursive: true);
		}
	}
}
