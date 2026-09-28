using System.Text.Json;
using System.Diagnostics;
using System.Xml.Linq;

using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("50 Debugger fixture")]
public sealed class DebuggerIntegrationTests
{
    readonly OpenDevelopAppFixture _app;

    public DebuggerIntegrationTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task ClassLibrary_DebugOptionsLaunchExternalHostAndHitLibraryBreakpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opendevelop-library-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "LibraryUnderTest.csproj");
        var librarySource = Path.Combine(directory, "LibraryCode.cs");
        var hostDirectory = Path.Combine(directory, "HostApp");
        Directory.CreateDirectory(hostDirectory);
        var hostProjectPath = Path.Combine(hostDirectory, "HostApp.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"LibraryCode.cs\" /></ItemGroup></Project>");
        File.WriteAllText(librarySource, "public static class LibraryCode\n{\n    public static string Run()\n    {\n        var result = \"library hit\";\n        return result;\n    }\n}\n");
        File.WriteAllText(hostProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Program.cs\" /><ProjectReference Include=\"../LibraryUnderTest.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(hostDirectory, "Program.cs"), "System.Console.WriteLine(LibraryCode.Run());");
        try
        {
            using (var build = Process.Start(new ProcessStartInfo("dotnet") {
                ArgumentList = { "build", hostProjectPath, "-c", "Debug", "--nologo" },
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!)
            {
                var output = await build.StandardOutput.ReadToEndAsync();
                var error = await build.StandardError.ReadToEndAsync();
                await build.WaitForExitAsync();
                Assert.True(build.ExitCode == 0, output + error);
            }
            var hostOutput = Path.Combine(hostDirectory, "bin", "Debug", "net10.0");
            var hostDll = Path.Combine(hostOutput, "HostApp.dll");
            Assert.True(File.Exists(hostDll));

            var opened = await _app.ReopenSolutionAsync(projectPath);
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
            var selected = await _app.InvokeAsync("od.project-browser.select", "Project", "LibraryUnderTest");
            Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());

            var result = await _app.InvokeAsync("od.project-browser.open-selected");
            Assert.True(result.GetProperty("projectOptionsOpen").GetBoolean(), result.ToString());
            Assert.Equal("LibraryUnderTest", result.GetProperty("projectName").GetString());
            Assert.Contains(result.GetProperty("tabs").EnumerateArray(), tab =>
                tab.GetString()?.Contains("Debug", StringComparison.OrdinalIgnoreCase) == true);

            var configured = await _app.InvokeAsync("od.project-options.configure-debug-host", hostDll, hostOutput);
            Assert.True(configured.GetProperty("success").GetBoolean(), configured.ToString());
            Assert.True(configured.GetProperty("libraryHintVisible").GetBoolean(), configured.ToString());
            Assert.True(configured.GetProperty("startable").GetBoolean(), configured.ToString());
            Assert.Equal("Program", configured.GetProperty("startAction").GetString());
            var projectXml = XDocument.Load(projectPath);
            Assert.Equal("Program", projectXml.Descendants("StartAction").Single().Value);
            Assert.Equal(hostDll, projectXml.Descendants("StartProgram").Single().Value);

            var breakpointLine = FindLine(librarySource, "var result = \"library hit\";");
            await _app.InvokeAsync("od.open-file", librarySource);
            await _app.InvokeAsync("od.debug.clear-breakpoints");
            var breakpoint = await _app.InvokeAsync("od.debug.set-breakpoint", librarySource, breakpointLine);
            Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());
            var shortcut = await _app.InvokeAsync("od.workbench.invoke-shortcut", "f5");
            Assert.True(shortcut.GetProperty("success").GetBoolean(), shortcut.ToString());
            JsonElement debug = default;
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                debug = await _app.InvokeAsync("od.debug.location");
                if (debug.GetProperty("stopped").GetBoolean()) break;
                await Task.Delay(200);
            }
            if (!debug.GetProperty("stopped").GetBoolean())
            {
                var output = await _app.InvokeAsync("od.debug.output");
                Assert.Fail($"The library breakpoint was not hit. Start: {debug}; Debug output: {output}");
            }
            Assert.Equal(breakpointLine, debug.GetProperty("currentLine").GetInt32());
            Assert.EndsWith("LibraryCode.cs", Normalize(debug.GetProperty("currentFile").GetString()));
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
            await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DebuggerService_IsRegisteredByAddIn()
    {
        var info = await _app.InvokeAsync("od.debug.service-info");

        Assert.True(info.GetProperty("available").GetBoolean());
        Assert.Equal("ICSharpCode.SharpDevelop.Services.WindowsDebugger", info.GetProperty("typeName").GetString());
        Assert.False(info.GetProperty("isDebugging").GetBoolean());

        var pads = await _app.InvokeAsync("od.pads");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.BreakPointsPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.CallStackPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.LocalVarPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.ThreadsPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.LoadedModulesPad");
    }

    [Fact]
    public async Task BreakpointHit_ExposesInspectionPadsThreadsModulesAndOutput()
    {
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        var breakpoint = await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);
        Assert.True(breakpoint.GetProperty("success").GetBoolean());
        Assert.Contains(breakpoint.GetProperty("lines").EnumerateArray(), l => l.GetInt32() == breakpointLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());
            Assert.EndsWith("Program.cs", Normalize(start.GetProperty("currentFile").GetString()));
            Assert.Equal(breakpointLine, start.GetProperty("currentLine").GetInt32());

            var stack = await _app.InvokeAsync("od.debug.call-stack");
            Assert.Contains(stack.EnumerateArray(), f => f.GetProperty("Name").GetString()!.Contains("Main"));

            var locals = await _app.InvokeAsync("od.debug.locals");
            Assert.Contains(locals.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "greeting"
                && v.GetProperty("Value").GetString()!.Contains("Hello, Debugger!"));
            Assert.Contains(locals.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "answer"
                && v.GetProperty("Value").GetString()!.Contains("42"));

            var evaluated = await _app.InvokeAsync("od.debug.evaluate", "answer");
            Assert.Equal("answer", evaluated.GetProperty("Name").GetString());
            Assert.Contains("42", evaluated.GetProperty("Value").GetString());

            var breakpointPad = await _app.InvokeAsync("od.debug.pad-snapshot", "BreakPointsPad");
            Assert.True(breakpointPad.GetProperty("found").GetBoolean());
            Assert.Contains(breakpointPad.GetProperty("items").EnumerateArray(), i =>
                Normalize(i.GetProperty("File").GetString()).EndsWith("Program.cs")
                && i.GetProperty("Line").GetInt32() == breakpointLine);

            var callStackPad = await _app.InvokeAsync("od.debug.pad-snapshot", "CallStackPad");
            Assert.True(callStackPad.GetProperty("found").GetBoolean());
            Assert.Contains(callStackPad.GetProperty("items").EnumerateArray(), f =>
                f.GetProperty("Name").GetString()!.Contains("Main"));

            var localsPad = await _app.InvokeAsync("od.debug.pad-snapshot", "LocalVarPad");
            Assert.True(localsPad.GetProperty("found").GetBoolean());
            Assert.Contains(localsPad.GetProperty("items").EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "answer"
                && v.GetProperty("Value").GetString()!.Contains("42"));

            var threadsPad = await _app.InvokeAsync("od.debug.pad-snapshot", "ThreadsPad");
            Assert.True(threadsPad.GetProperty("found").GetBoolean());
            Assert.NotEmpty(threadsPad.GetProperty("items").EnumerateArray());

            var modulesPad = await _app.InvokeAsync("od.debug.pad-snapshot", "LoadedModulesPad");
            Assert.True(modulesPad.GetProperty("found").GetBoolean());
            Assert.Equal(JsonValueKind.Array, modulesPad.GetProperty("items").ValueKind);

            var threads = await _app.InvokeAsync("od.debug.threads");
            Assert.NotEmpty(threads.EnumerateArray());

            var modules = await _app.InvokeAsync("od.debug.modules");
            Assert.NotEmpty(modules.EnumerateArray());

            var output = await _app.InvokeAsync("od.debug.output");
            Assert.NotEmpty(output.GetProperty("text").GetString()!);
            Assert.Equal("Debug", output.GetProperty("selectedCategory").GetString());
            Assert.True(output.GetProperty("isOutputVisible").GetBoolean());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task SharpDbgVisualizers_OfferAndRenderTextXmlAndCollectionValues()
    {
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        var breakpoint = await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);
        Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            var text = await _app.InvokeAsync("od.debug.visualizer.inspect", "greeting", "Text visualizer");
            Assert.True(text.GetProperty("success").GetBoolean(), text.ToString());
            Assert.Contains("Text visualizer", text.GetProperty("availableVisualizers").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("greeting", text.GetProperty("title").GetString());
            Assert.Contains("Hello, Debugger!", text.GetProperty("text").GetString());

            var xml = await _app.InvokeAsync("od.debug.visualizer.inspect", "xml", "XML visualizer");
            Assert.True(xml.GetProperty("success").GetBoolean(), xml.ToString());
            Assert.Contains("Text visualizer", xml.GetProperty("availableVisualizers").EnumerateArray().Select(x => x.GetString()));
            Assert.Contains("XML visualizer", xml.GetProperty("availableVisualizers").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("xml", xml.GetProperty("title").GetString());
            Assert.Contains("<root><item>visualizer</item></root>", xml.GetProperty("text").GetString());
            Assert.Contains("XML", xml.GetProperty("highlighting").GetString(), StringComparison.OrdinalIgnoreCase);

            var grid = await _app.InvokeAsync("od.debug.visualizer.inspect", "numbers", "Collection visualizer");
            Assert.True(grid.GetProperty("success").GetBoolean(), grid.ToString());
            Assert.Equal(new[] { "Collection visualizer" }, grid.GetProperty("availableVisualizers").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("numbers", grid.GetProperty("title").GetString());
            Assert.Contains(grid.GetProperty("rows").EnumerateArray(), row =>
                row.GetProperty("Name").GetString() == "[0]" && row.GetProperty("Value").GetString()!.Contains("7"));
            Assert.Contains(grid.GetProperty("rows").EnumerateArray(), row =>
                row.GetProperty("Name").GetString() == "[1]" && row.GetProperty("Value").GetString()!.Contains("11"));
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task StepIntoAndStepOver_UpdateCurrentFrameAndLocals()
    {
        var program = ProgramPath;
        var callLine = FindLine(program, "var message = ComputeGreeting(\"World\");");
        var writeLine = FindLine(program, "Console.WriteLine(message);");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, callLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            var stepInto = await _app.InvokeAsync("od.debug.step-into", 30);
            Assert.True(stepInto.GetProperty("stopped").GetBoolean(), stepInto.ToString());

            var stackAfterStepInto = await WaitForTopFrameNameAsync("ComputeGreeting", 5);
            var topFrameAfterStepInto = stackAfterStepInto.EnumerateArray().First();
            Assert.Contains("ComputeGreeting", topFrameAfterStepInto.GetProperty("Name").GetString());
            Assert.Contains(stackAfterStepInto.EnumerateArray(), f => f.GetProperty("Name").GetString()!.Contains("Main"));

            var localsInsideMethod = await _app.InvokeAsync("od.debug.locals");
            Assert.Contains(localsInsideMethod.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "name"
                && v.GetProperty("Value").GetString()!.Contains("World"));

            var stepOut = await _app.InvokeAsync("od.debug.step-out", 30);
            Assert.True(stepOut.GetProperty("stopped").GetBoolean(), stepOut.ToString());
            Assert.True(stepOut.GetProperty("currentLine").GetInt32() >= callLine);

            var stepOver = await _app.InvokeAsync("od.debug.step-over", 30);
            Assert.True(stepOver.GetProperty("stopped").GetBoolean(), stepOver.ToString());
            var topFrameAfterStepOver = await WaitForTopFrameLineAsync(writeLine, 5);
            Assert.Equal(writeLine, topFrameAfterStepOver.GetProperty("Line").GetInt32());

            var localsAfterStepOver = await _app.InvokeAsync("od.debug.locals");
            Assert.Contains(localsAfterStepOver.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "message"
                && v.GetProperty("Value").GetString()!.Contains("Hello, World!"));
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task ContinueDebug_HitsSecondBreakpoint()
    {
        var program = ProgramPath;
        var firstLine = FindLine(program, "var message = ComputeGreeting(\"World\");");
        var secondLine = FindLine(program, "Console.WriteLine(message);");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, firstLine);
        await _app.InvokeAsync("od.debug.set-breakpoint", program, secondLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());
            Assert.Equal(firstLine, start.GetProperty("currentLine").GetInt32());

            var cont = await _app.InvokeAsync("od.debug.continue", true, 30);
            Assert.True(cont.GetProperty("stopped").GetBoolean(), cont.ToString());
            Assert.Equal(secondLine, cont.GetProperty("currentLine").GetInt32());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task DebugStart_SwitchesToDebugLayout_AndStopRestoresDefault_KeepingPadsOpen()
    {
        // Regression coverage for the debug-session layout switching (doc/technotes/ilspy.md
        // "Legacy pad migration", 2026-08-09):
        //  - BaseDebuggerService.OnDebugStarting switches the workbench to the "Debug" layout,
        //    and that switch must NOT evict pads the user had open (the incremental layout
        //    contract - previously the Debug layout switch *closed* ErrorList & co).
        //  - WindowsDebugger.Stop() used to skip SessionExited entirely for an explicit stop
        //    (DapSession.CleanupSession never raises Exited), so OnDebugStopped never ran and the
        //    workbench stayed on "Debug" forever - the fix calls SessionExited() after
        //    CurrentSession.Stop(), which must switch the layout back to "Default".
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            // While stopped at the breakpoint, the debugger's automatic layout switch is active:
            var layoutDuring = await _app.InvokeAsync("od.layout.current-name");
            Assert.Equal("Debug", layoutDuring.GetProperty("layoutName").GetString());

            // ...and the pads that were open before the session started must still be docked.
            var during = await _app.InvokeAsync("od.layout.tool-panes");
            var visibleDuring = VisibleContentIds(during).ToHashSet();
            Assert.Contains("ProjectBrowser", visibleDuring);
            Assert.Contains("OutputPad", visibleDuring);
            Assert.Contains("ErrorList", visibleDuring);

            await _app.InvokeAsync("od.debug.stop");

            // The explicit-stop fix: the layout must switch back to Default on its own.
            var layoutAfter = await _app.InvokeAsync("od.layout.current-name");
            Assert.Equal("Default", layoutAfter.GetProperty("layoutName").GetString());

            var after = await _app.InvokeAsync("od.layout.tool-panes");
            var visibleAfter = VisibleContentIds(after).ToHashSet();
            Assert.Contains("ProjectBrowser", visibleAfter);
            Assert.Contains("OutputPad", visibleAfter);
            Assert.Contains("ErrorList", visibleAfter);

            var info = await _app.InvokeAsync("od.debug.service-info");
            Assert.False(info.GetProperty("isDebugging").GetBoolean());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    static IEnumerable<string> VisibleContentIds(JsonElement toolPanes)
        => toolPanes.GetProperty("panes").EnumerateArray()
            .Where(p => p.GetProperty("IsVisible").GetBoolean())
            .Select(p => p.GetProperty("ContentId").GetString()!);

    [Fact]
    public async Task DebugStart_WhenTargetMissing_FailsCleanlyInsteadOfHanging()
    {
        // Regression coverage for the premature-exit/adapter-failure bug: WindowsDebugger.StartAsync
        // used to leave the paused-line marker in place and the toolbar in a "still debugging"-looking
        // state if the session died right after starting (e.g. SharpDbg's CommandUnknownException
        // when handed an apphost path, or the debuggee's target framework being missing). We can't
        // easily force those exact OS/runtime-specific failures on demand, but WindowsDebugger.StartAsync
        // takes the same catch-block path (clear marker, print "ERROR: ...", activate the Debug output
        // channel, stop) whenever the debug target can't be launched at all - so making the target
        // executable briefly disappear exercises that same code path deterministically.
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);

        string outputDir = Path.Combine(Path.GetDirectoryName(_app.DebugTestProjectPath)!, "bin", "Debug", "net10.0");
        var moved = new List<(string From, string To)>();
        foreach (string file in Directory.GetFiles(outputDir, "DebugTestApp.*"))
        {
            string away = file + ".movedfortest";
            File.Move(file, away);
            moved.Add((file, away));
        }

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 20);

            // Must return promptly reporting failure - not hang, not report a phantom "still debugging".
            Assert.False(start.GetProperty("started").GetBoolean(), start.ToString());
            Assert.False(start.GetProperty("isDebugging").GetBoolean(), start.ToString());

            var info = await _app.InvokeAsync("od.debug.service-info");
            Assert.False(info.GetProperty("isDebugging").GetBoolean());
            Assert.False(info.GetProperty("isProcessRunning").GetBoolean());

            var output = await _app.InvokeAsync("od.debug.output");
            Assert.Contains("ERROR", output.GetProperty("text").GetString());
        }
        finally
        {
            foreach (var (from, to) in moved)
                File.Move(to, from);
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    string ProgramPath => Path.Combine(Path.GetDirectoryName(_app.DebugTestProjectPath)!, "Program.cs");

    static int FindLine(string path, string marker)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        }
        throw new InvalidOperationException($"Marker '{marker}' not found in {path}.");
    }

    static string Normalize(string? path) => (path ?? string.Empty).Replace('\\', '/');

    async Task<JsonElement> WaitForTopFrameLineAsync(int expectedLine, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        JsonElement result = default;
        while (DateTime.UtcNow < deadline)
        {
            var stack = await _app.InvokeAsync("od.debug.call-stack");
            result = stack.EnumerateArray().First();
            if (result.GetProperty("Line").GetInt32() == expectedLine)
                break;
            await Task.Delay(100);
        }
        return result;
    }

    async Task<JsonElement> WaitForTopFrameNameAsync(string expectedName, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        JsonElement result = default;
        while (DateTime.UtcNow < deadline)
        {
            result = await _app.InvokeAsync("od.debug.call-stack");
            var frames = result.EnumerateArray().ToArray();
            if (frames.Length > 0 && frames[0].GetProperty("Name").GetString()!.Contains(expectedName))
                break;
            await Task.Delay(100);
        }
        return result;
    }
}
