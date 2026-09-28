using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("20 General workbench fixture")]
public sealed class T4IntegrationTests
{
    readonly OpenDevelopAppFixture _app;

    public T4IntegrationTests(OpenDevelopAppFixture app) => _app = app;

    [Fact]
    public async Task CSharpOutputTemplate_HighlightsBodyAndControlCode_AndGeneratesFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opendevelop-t4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "T4Fixture.csproj");
        var templatePath = Path.Combine(directory, "Generated.tt");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><None Include=\"Generated.tt\" Generator=\"TextTemplatingFileGenerator\" /></ItemGroup></Project>");
        File.WriteAllText(templatePath, "<#@ template language=\"C#\" #>\n<#@ output extension=\".cs\" #>\npublic class Generated { public string Name => \"Hello\"; }\n<# int answer = 42; #>\n");
        try
        {
            var opened = await _app.ReopenSolutionAsync(projectPath);
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
            var editor = await _app.InvokeAsync("od.open-file", templatePath);
            Assert.True(editor.GetProperty("opened").GetBoolean(), editor.ToString());
            var highlighting = await _app.InvokeAsync("od.t4.highlighting");
            Assert.True(highlighting.GetProperty("success").GetBoolean(), highlighting.ToString());
            Assert.Equal("TextTemplating (code: C#, output: C#)", highlighting.GetProperty("definition").GetString());
            AssertColored(highlighting, "class");
            AssertColored(highlighting, "int");
            AssertColored(highlighting, "<#");

            var generated = Path.Combine(directory, "Generated.cs");
            var command = await _app.InvokeAsync("od.menu.invoke", "ICSharpCode.TextTemplating.GenerateT4Command");
            Assert.True(command.GetProperty("success").GetBoolean(), command.ToString());
            for (var attempt = 0; attempt < 50 && !File.Exists(generated); attempt++)
                await Task.Delay(100);
            Assert.True(File.Exists(generated), "T4 command did not generate Generated.cs: " + command);
            Assert.Contains("public class Generated", File.ReadAllText(generated));

            File.WriteAllText(Path.Combine(directory, "Plain.tt"), "<#@ template language=\"C#\" #>\n<#@ output extension=\".txt\" #>\nclass is literal output text\n<# int answer = 42; #>\n");
            editor = await _app.InvokeAsync("od.open-file", Path.Combine(directory, "Plain.tt"));
            Assert.True(editor.GetProperty("opened").GetBoolean(), editor.ToString());
            highlighting = await _app.InvokeAsync("od.t4.highlighting");
            Assert.True(highlighting.GetProperty("success").GetBoolean(), highlighting.ToString());
            Assert.Equal("TextTemplating (code: C#, output: none)", highlighting.GetProperty("definition").GetString());
            AssertColored(highlighting, "int");
            Assert.DoesNotContain(highlighting.GetProperty("sections").EnumerateArray(), section =>
                section.GetProperty("text").GetString() == "class");

            var replaced = await _app.InvokeAsync("od.file.replace-text", Path.Combine(directory, "Plain.tt"),
                "extension=\".txt\"", "extension=\".cs\"");
            Assert.True(replaced.GetProperty("success").GetBoolean(), replaced.ToString());
            highlighting = await _app.InvokeAsync("od.t4.highlighting");
            Assert.Equal("TextTemplating (code: C#, output: C#)", highlighting.GetProperty("definition").GetString());
            AssertColored(highlighting, "class");

            var unregisteredPath = Path.Combine(directory, "Unregistered.tt");
            File.WriteAllText(unregisteredPath, "<#@ template language=\"Unregistered\" #>\n<#@ output extension=\".cs\" #>\npublic class OutputOnly { }\n");
            editor = await _app.InvokeAsync("od.open-file", unregisteredPath);
            Assert.True(editor.GetProperty("opened").GetBoolean(), editor.ToString());
            highlighting = await _app.InvokeAsync("od.t4.highlighting");
            Assert.Equal("TextTemplating (code: none, output: C#)", highlighting.GetProperty("definition").GetString());
            AssertColored(highlighting, "class");

            var neutralPath = Path.Combine(directory, "Neutral.tt");
            File.WriteAllText(neutralPath, "<#@ template language=\"Unregistered\" #>\n<#@ output extension=\".unknown\" #>\nclass remains template text\n");
            editor = await _app.InvokeAsync("od.open-file", neutralPath);
            Assert.True(editor.GetProperty("opened").GetBoolean(), editor.ToString());
            highlighting = await _app.InvokeAsync("od.t4.highlighting");
            Assert.Equal("TextTemplating", highlighting.GetProperty("definition").GetString());
        }
        finally
        {
            await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VBAndFSharpTemplates_HighlightControlCodeAndOutputIndependently()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opendevelop-t4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "T4Fixture.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        try
        {
            var opened = await _app.ReopenSolutionAsync(projectPath);
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

            var vbPath = Path.Combine(directory, "Generated.vb.tt");
            File.WriteAllText(vbPath, "<#@ template language=\"VB\" #>\n<#@ output extension=\".vb\" #>\nPublic Class Generated\nEnd Class\n<# Dim answer As Integer = 42 #>\n");
            var editor = await _app.InvokeAsync("od.open-file", vbPath);
            Assert.True(editor.GetProperty("opened").GetBoolean(), editor.ToString());
            var highlighting = await _app.InvokeAsync("od.t4.highlighting");
            Assert.True(highlighting.GetProperty("success").GetBoolean(), highlighting.ToString());
            Assert.Equal("TextTemplating (code: VB, output: VB)", highlighting.GetProperty("definition").GetString());
            AssertColored(highlighting, "Class");
            AssertColored(highlighting, "Dim");

            // T4 has no F# template language: C# control blocks generating an F# file.
            var fsPath = Path.Combine(directory, "Generated.fs.tt");
            File.WriteAllText(fsPath, "<#@ template language=\"C#\" #>\n<#@ output extension=\".fs\" #>\nlet answer = 42\n<# int count = 1; #>\n");
            editor = await _app.InvokeAsync("od.open-file", fsPath);
            Assert.True(editor.GetProperty("opened").GetBoolean(), editor.ToString());
            highlighting = await _app.InvokeAsync("od.t4.highlighting");
            Assert.True(highlighting.GetProperty("success").GetBoolean(), highlighting.ToString());
            Assert.Equal("TextTemplating (code: C#, output: F#)", highlighting.GetProperty("definition").GetString());
            // FS-Mode.xshd is a v1 definition whose colors are unnamed, so check the token itself.
            Assert.Contains(highlighting.GetProperty("sections").EnumerateArray(), section =>
                section.GetProperty("text").GetString() == "let");
            AssertColored(highlighting, "int");
            // The control block ends at "#>": C#'s "#" preprocessor span must not swallow it.
            Assert.Contains(highlighting.GetProperty("sections").EnumerateArray(), section =>
                section.GetProperty("text").GetString() == "#>"
                && section.GetProperty("color").GetString() == "TemplateBlockStartEndTags");
        }
        finally
        {
            await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    static void AssertColored(JsonElement highlighting, string token)
        => Assert.True(highlighting.GetProperty("sections").EnumerateArray().Any(section =>
                section.GetProperty("text").GetString()?.Contains(token, StringComparison.Ordinal) == true
                && !string.IsNullOrEmpty(section.GetProperty("color").GetString())),
            $"'{token}' is not colored. Sections: {highlighting.GetProperty("sections")}");
}
