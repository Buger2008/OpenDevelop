# T4 (Text Template Transformation Toolkit) — Port Status

Ported from MonoDevelop's T4 addin (`externals/monodevelop/main/src/addins/TextTemplating`), since
it's simpler and less AppDomain-dependent than SharpDevelop's own T4 addin
(`externals/SharpDevelop/src/AddIns/Misc/TextTemplating`, ~40 files, deeply AppDomain-based).
Even MonoDevelop's own host isn't fully AppDomain-free (see §1), but the actual UnoDevelop port
runs entirely in-process — no AppDomain isolation at all, which is the only thing that ever made
this genuinely hard, not anything Uno itself lacks.

## 1. Why the `#if !HAS_UNO` guards, and what they're *not* about

Two different reasons code got excluded here — worth keeping straight, since only one of them is
a real platform limitation:

- **Genuinely platform-incompatible** (needs a guard, no way around it): `AppDomain`-based
  isolation. Both SharpDevelop's and MonoDevelop's T4 hosts use a `TemplatingAppDomainRecycler`
  (recyclable AppDomain pool, so `<#@ assembly #>` references can be unloaded without restarting
  the IDE) — `AppDomain.CreateDomain`/cross-domain marshaling doesn't exist on .NET Core the way
  it did on .NET Framework. **The actual fix wasn't a guard — it was not needing the recycler at
  all**: `UnoTextTemplatingHost` (`src/Main/SharpDevelop/AddIns/TextTemplating/UnoTextTemplatingHost.cs`)
  already extended `Mono.TextTemplating.TemplateGenerator` directly and runs fully in-process.
  `Mono.TextTemplating` (already a `PackageReference` in this project) is the real, portable T4
  engine — the same one `dotnet-t4` and modern VS use. In-process execution accepts one
  trade-off both old hosts avoided: an assembly referenced by a `<#@ assembly #>` directive stays
  loaded for the process lifetime (no unload without restarting UnoDevelop). That's the standard
  modern-T4-host trade-off, not a regression specific to this port.
  Two small remaining call sites needed fixing, not guarding — `ProjectFileTemplatingHost` was
  never `IDisposable` (`TemplateGenerator` doesn't implement it; a stray `using var` in
  `TextTemplatingFilePreprocessor.cs` was just wrong) and `host.Session` needed to go through
  `Mono.TextTemplating.TemplateGenerator`'s explicit interface implementation
  (`((ITextTemplatingSessionHost)host).Session`), not a plain property access.
- **Not actually a Uno limitation — just an incomplete port** (fixed by adding the missing
  piece, not by guarding it out): `CustomTool.cs`'s `ExecuteCustomToolCommand` uses the WPF-only
  `FileNode` type, and `ProjectBrowserPad.RefreshViewAsync()` is a WPF pad — those needed real
  `#if !HAS_UNO` guards (rewritten separately for Uno, see §2). But
  `FileUtility.ObservedSave`/`FileSaved`/`FileErrorPolicy`, and the old static `ProjectService`/
  `FileService` facade calls, are **plain, portable C#** with no WPF dependency at all — they
  were just never included in the trimmed `FileUtility.Minimal.cs` that `ICSharpCode.Core.Uno.csproj`
  compiles instead of the full `FileUtility.cs` (which has its own overlapping-but-different
  member set vs. this codebase's `FileUtility.uno.cs`, so it can't just be swapped in wholesale —
  see the compile errors that approach produced). The actual fix: a new
  `FileUtility.ObservedSave.uno.cs` with just the missing slice (`ObservedSave` both overload
  families, `FileSaved`/`RaiseFileSaved`, `FileErrorPolicy`), and inlining the two one-line
  `ProjectService`/`FileService` static-facade calls in `CustomTool.cs` as their DI equivalents
  (`project.Items.Add(...)`, `IFileService.FireFileCreated(...)`) under `#if HAS_UNO`. So: not
  "Uno can't support this," but "this specific slice of `FileUtility` hadn't been ported yet" —
  now it has.

## 2. What's done

- **In-process T4 generation/preprocessing** — `TextTemplatingFileGenerator`/
  `TextTemplatingFilePreprocessor` (already existed), using `Mono.TextTemplating.TemplateGenerator`
  directly, no AppDomain.
- **Project-wide "Process T4 Templates..."** — `GenerateT4Command`, Tools menu.
- **Per-file "Run Custom Tool"** — new `RunT4CustomToolSolutionExplorerCommand`, Solution
  Explorer file-node context menu (shown for every file, no-ops for non-`.tt`/`.t4` files — no
  per-extension condition evaluator exists yet to gate menu visibility itself; a real limitation,
  not a T4-specific one).
- **"Add > New Item... > Text Template (.tt)"** — now available via the
  `Microsoft.TemplateEngine`-based template system (`externals/OpenDevelop/doc/technotes/template-system.md`): UnoDevelop ships a
  bundled first-party item template package (`Templates/Bundled/TextTemplate`) and auto-installs
  it if missing, so clean environments can create `.tt` files without separately running
  `dotnet new install`.
- **Real `CustomToolsService`** (linked from `externals/SharpDevelop/src/Main/Base/Project/Src/Project/CustomTool.cs`,
  `BeforeBuildCustomToolRunner.cs`, `BeforeBuildCustomToolProjectItems.cs`,
  `BeforeBuildCustomToolFileNameFilter.cs`, `ProjectCustomToolOptions.cs`) — the actual upstream
  mechanism, not a hand-rolled substitute:
  - `FileProjectItem.CustomTool` (already existed, reads/writes the `Generator` MSBuild metadata)
    is the source of truth for which tool runs on a file.
  - **Auto-run-on-save**: `CustomToolsService.Initialize()` subscribes to `FileUtility.FileSaved`;
    saving a file with `CustomTool` set re-runs it automatically.
  - **Auto-run-before-build**: `BeforeBuildCustomToolRunner` subscribes to
    `SD.BuildService.BuildStarted`, gated per-project by `ProjectCustomToolOptions` (the existing
    "run custom tools on build" project setting, `customTool/runOnBuild` + `fileNames`
    preferences — no new UI needed, this reads properties the original SharpDevelop project
    options panel already wrote if a project had one, or can be set programmatically).
  - `TextTemplatingFileGeneratorCustomTool`/`TextTemplatingFilePreprocessorCustomTool` are the
    two `ICustomTool` implementations registered under `/SharpDevelop/CustomTools` in
    `UnoDevelop.TextTemplating.addin`, replacing the old ad-hoc "check `Generator` metadata by
    hand" dispatch in `T4TemplateRunner` — that now just calls
    `CustomToolsService.GetCustomTool`/`RunCustomTool`, the same path auto-run-on-save uses, so
    manual "Run Custom Tool" and automatic save-triggered runs are guaranteed to behave
    identically (same descriptor lookup, same `ICustomTool.GenerateCode`).
  - `ProjectBrowserPad.RefreshViewAsync()` (upstream's manual "refresh the tree after adding a
    generated file") isn't needed on Uno — `project.Items.Add(...)` already triggers
    `IProjectService.ProjectItemAdded`, which `MainPage.OnProjectItemAdded` already handles
    (in-place Solution Explorer node refresh, *and* the incremental Roslyn document-add from
    externals/OpenDevelop/doc/technotes/language-services.md §4 slice 3 — a T4-generated `.cs` file gets picked up by the
    language service the same way a hand-created one would).
  - `SD.ParserService.ParseAsync(...)` (upstream's "tell the parser about the newly generated
    file immediately") is dropped — no `IParserService` is registered in this codebase
    (`language-services.md`'s `ILanguageService`/Roslyn replaced that whole system); the next
    access to the generated file's language service naturally re-reads current content, so this
    wasn't replacing lost functionality, just an eager-refresh optimization that doesn't apply
    to a different architecture.

## 3. What's still skipped, and why each one is a genuinely bigger lift (not just a guard)

### Mixed T4 / generated C# highlighting (OpenDevelop)

The T4 addin registers only language-neutral directives and `<# ... #>` delimiters
for `.tt`, `.t4`, and `.ttinclude`. It does not import C# or own a C# grammar.
Language addins opt in with one `<T4SyntaxMode>` codon under
`/SharpDevelop/ViewContent/AvalonEdit/T4SyntaxModes` (doozer in AvalonEdit.AddIn):

```xml
<T4SyntaxMode id="C#" language="C#;C#v3.5;CSharp" outputExtensions=".cs" syntaxMode="C#" />
```

VBBinding registers `language="VB;VBv3.5;VisualBasic" outputExtensions=".vb"` against
AvalonEdit's built-in `VB` mode. FSharpBinding registers only `outputExtensions=".fs;.fsi;.fsx"`:
T4 has no F# template language, so an F# template body pairs with C# or VB control blocks.

`language` lists the T4 `template language` values whose `<# ... #>` blocks use
`syntaxMode`; `outputExtensions` lists the T4 `output extension` values whose
template body uses it. Either may be omitted. `T4HighlightingComposer` builds the
definition at runtime from the base `TextTemplating` definition: the directive
block keeps its own rules, the control block takes the code language's main rule
set, and the body imports the output language's main rule set. The two directives
are independent per the T4 specification, so a VB control block generating a
`.cs` file combines the VB and C# registrations without a dedicated pair. No
per-language xshd is needed, and the template file remains `.tt`.

AvalonEdit lets a nested span win a tie against its parent span's end, so an embedded
C# rule set would start its `#` preprocessor span on the closing `#>` and keep the rest
of the line in C#. The composer therefore guards the code rule set's top-level spans and
rules with a negative lookahead for the control block's end expression. Output-language
spans are imported as-is, so a T4 block inside an output string literal (for example
`"<#= name #>"` in `.cs` output) is still highlighted as part of the string.

The registration must live in the language addin, which depends on T4: AvalonEdit
has its own built-in `C#` definition, so resolving `GetDefinition("C#")` directly
would keep C# highlighting in T4 even with CSharpBinding disabled. Only codons
from enabled addins are built, so disabling a language addin removes its T4
highlighting without changing T4 core. The composed definition is named
`TextTemplating (code: X, output: Y)`; without any match it is plain `TextTemplating`.
Editing the output directive switches the selected definition without reopening
the file.

This is lexical highlighting, not Roslyn analysis of an embedded C# document.
Completion, diagnostics, and semantic colors inside T4 remain separate work.


Unlike §1's items, these need *new* infrastructure this codebase doesn't have yet — not just
adapting an upstream file:

- **`T4Parser` + `T4ParsedDocument`** (MonoDevelop:
  `MonoDevelop.TextTemplating/Parser/T4Parser.cs`, `T4ParsedDocument.cs`) — plugs T4 files into
  MonoDevelop's generic `TypeSystemParser`/`DefaultParsedDocument` abstraction (folding regions
  for `<# #>` blocks, error squiggles for directive syntax errors, an outline). UnoDevelop has no
  equivalent generic "parsed document" abstraction for non-C#/VB files — `ILanguageService`
  (`language-services.md`) is the closest thing, but it's shaped around Roslyn/LSP semantic
  analysis, not simple directive-block parsing. Two real options: (a) write a small standalone T4
  directive parser directly against UnoEdit's folding/highlighting hooks (bypassing
  `ILanguageService` entirely — T4 files don't need semantic analysis, just structural
  understanding of `<# #>`/`<#@ #>`/`<#= #>` blocks), or (b) add a minimal `ILanguageService`
  implementation for `.tt` that only implements `GetDocumentOutlineAsync`/diagnostics and stubs
  the rest. (a) is simpler and matches what T4 actually needs.
- **`T4EditorExtension`** (MonoDevelop: `MonoDevelop.TextTemplating/Gui/T4EditorExtension.cs`) —
  C# completion *inside* `<# #>` blocks (i.e., typing C# in a template gets real IntelliSense).
  Needs embedding a Roslyn completion session scoped to just the block contents, which is
  meaningfully different from `CSharpVBLanguageService`'s whole-file model — this is its own
  design problem, not a quick reuse of existing `ILanguageService` completion.

None of these block the core "write a `.tt` file, it generates code, saving/building re-runs it"
workflow from §2 — they're editor-experience and project-scaffolding polish on top of it.
