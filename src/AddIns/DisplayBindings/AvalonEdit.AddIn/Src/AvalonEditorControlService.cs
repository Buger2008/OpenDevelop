// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.AddIn.Options;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.AvalonEdit;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// Implementation of IEditorControlService, allows other addins to create editors or access the options without
	/// requiring a reference to AvalonEdit.AddIn.
	/// </summary>
	public class AvalonEditorControlService : IEditorControlService
	{
		static readonly Regex t4TemplateLanguage = new Regex(
			@"<#@\s*template\b(?:(?!#>).)*\blanguage\s*=\s*(?:""(?<value>[^""]+)""|'(?<value>[^']+)')",
			RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
		static readonly Regex t4OutputExtension = new Regex(
			@"<#@\s*output\b(?:(?!#>).)*\bextension\s*=\s*(?:""(?<value>\.[^""]+)""|'(?<value>\.[^']+)')",
			RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

		public static IHighlightingDefinition GetHighlightingDefinition(IDocument document)
		{
			if (document.FileName == null)
				return null;
			string extension = Path.GetExtension(document.FileName);
			if (extension.Equals(".tt", StringComparison.OrdinalIgnoreCase)
				|| extension.Equals(".t4", StringComparison.OrdinalIgnoreCase)
				|| extension.Equals(".ttinclude", StringComparison.OrdinalIgnoreCase)) {
				string language = t4TemplateLanguage.Match(document.Text).Groups["value"].Value;
				string outputExtension = t4OutputExtension.Match(document.Text).Groups["value"].Value;
				if (string.IsNullOrEmpty(language)) language = "C#"; // T4 engine default
				if (string.IsNullOrEmpty(outputExtension)) outputExtension = ".cs";
				var composed = T4HighlightingComposer.GetDefinition(language, outputExtension);
				if (composed != null) return composed;
			}
			return HighlightingManager.Instance.GetDefinitionByExtension(extension);
		}

		public ITextEditorOptions GlobalOptions {
			get { return CodeEditorOptions.Instance; }
		}
		
		public ITextEditor CreateEditor(out object control)
		{
			SharpDevelopTextEditor editor = new SharpDevelopTextEditor();
			control = editor;
			return new CodeCompletionEditorAdapter(editor);
		}
		
		public IHighlighter CreateHighlighter(IDocument document)
		{
			if (document.FileName == null)
				return new MultiHighlighter(document);
			var def = GetHighlightingDefinition(document);
			if (def == null)
				return new MultiHighlighter(document);
			List<IHighlighter> highlighters = new List<IHighlighter>();
			var textDocument = document as TextDocument;
			if (textDocument != null) {
				highlighters.Add(new DocumentHighlighter(textDocument, def));
			}
			// add additional highlighters
			highlighters.AddRange(SD.AddInTree.BuildItems<IHighlighter>(HighlighterDoozer.AddInPath, document, false));
			var multiHighlighter = new MultiHighlighter(document, highlighters.ToArray());
			return new CustomizingHighlighter(multiHighlighter, CustomizedHighlightingColor.FetchCustomizations(def.Name));
		}
	}

	public class HighlighterDoozer : IDoozer
	{
		internal const string AddInPath = "/SharpDevelop/ViewContent/AvalonEdit/Highlighters";
		
		public bool HandleConditions {
			get { return false; }
		}
		
		public object BuildItem(BuildItemArgs args)
		{
			if (!(args.Parameter is IDocument))
				throw new ArgumentException("Caller must be IDocument!");
			Codon codon = args.Codon;
			if (!codon.Properties["extensions"].Split(';').Contains(Path.GetExtension(((IDocument)args.Parameter).FileName)))
				return null;
			return Activator.CreateInstance(codon.AddIn.FindType(codon.Properties["class"]), args.Parameter);
		}
	}

}
