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
using System.Linq;
using System.Text.RegularExpressions;

using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.Core;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// A language AddIn's offer to highlight T4 templates with one of its syntax modes.
	/// </summary>
	public class T4SyntaxMode
	{
		public string[] Languages { get; private set; }
		public string[] OutputExtensions { get; private set; }
		public string SyntaxMode { get; private set; }

		public T4SyntaxMode(string[] languages, string[] outputExtensions, string syntaxMode)
		{
			this.Languages = languages;
			this.OutputExtensions = outputExtensions;
			this.SyntaxMode = syntaxMode;
		}
	}

	/// <summary>
	/// Lets a language AddIn contribute its syntax mode to T4 templates. The registration lives
	/// in the language AddIn, so disabling that AddIn also removes its T4 mixed highlighting.
	/// </summary>
	/// <attribute name="syntaxMode" use="required">
	/// Name of a registered syntax mode, e.g. "C#".
	/// </attribute>
	/// <attribute name="language" use="optional">
	/// Semicolon-separated values of &lt;#@ template language="..." #&gt; whose control blocks
	/// (&lt;# ... #&gt;) are highlighted with the syntax mode.
	/// </attribute>
	/// <attribute name="outputExtensions" use="optional">
	/// Semicolon-separated values of &lt;#@ output extension="..." #&gt; whose template body
	/// (the text outside control blocks) is highlighted with the syntax mode.
	/// </attribute>
	/// <usage>Only in /SharpDevelop/ViewContent/AvalonEdit/T4SyntaxModes</usage>
	/// <returns>A T4SyntaxMode object.</returns>
	public class T4SyntaxModeDoozer : IDoozer
	{
		public const string Path = "/SharpDevelop/ViewContent/AvalonEdit/T4SyntaxModes";

		public bool HandleConditions {
			get { return false; }
		}

		public object BuildItem(BuildItemArgs args)
		{
			Codon codon = args.Codon;
			return new T4SyntaxMode(Split(codon.Properties["language"]),
			                        Split(codon.Properties["outputExtensions"]),
			                        codon.Properties["syntaxMode"]);
		}

		static string[] Split(string list)
		{
			return string.IsNullOrEmpty(list) ? Array.Empty<string>() : list.Split(';');
		}
	}

	/// <summary>
	/// Builds the highlighting for a T4 template from the TextTemplating base definition plus the
	/// syntax modes registered for its template language (control blocks) and output extension
	/// (template body).
	/// </summary>
	public static class T4HighlightingComposer
	{
		const string BaseDefinitionName = "TextTemplating";

		public static IHighlightingDefinition GetDefinition(string language, string outputExtension)
		{
			var baseDefinition = HighlightingManager.Instance.GetDefinition(BaseDefinitionName);
			if (baseDefinition == null)
				return null;
			var modes = AddInTree.BuildItems<T4SyntaxMode>(T4SyntaxModeDoozer.Path, null, false);
			var code = Find(modes, m => m.Languages, language);
			var output = Find(modes, m => m.OutputExtensions, outputExtension);
			if (code == null && output == null)
				return baseDefinition;

			var mainRuleSet = new HighlightingRuleSet();
			foreach (var span in baseDefinition.MainRuleSet.Spans) {
				// The directive block (<#@ ... #>) keeps its own rule set; the control block
				// (<# ... #>) takes the template language's rules.
				mainRuleSet.Spans.Add(span.RuleSet != null ? span : new HighlightingSpan {
					StartExpression = span.StartExpression,
					EndExpression = span.EndExpression,
					StartColor = span.StartColor,
					SpanColor = span.SpanColor,
					EndColor = span.EndColor,
					SpanColorIncludesStart = span.SpanColorIncludesStart,
					SpanColorIncludesEnd = span.SpanColorIncludesEnd,
					RuleSet = code != null ? GuardEnd(code.MainRuleSet, span.EndExpression) : null
				});
			}
			if (output != null) {
				// Same as <Import ruleSet="..."/> in xshd: the body uses the output language's rules.
				foreach (var span in output.MainRuleSet.Spans)
					mainRuleSet.Spans.Add(span);
				foreach (var rule in output.MainRuleSet.Rules)
					mainRuleSet.Rules.Add(rule);
			}
			string name = BaseDefinitionName + " (code: " + (code != null ? code.Name : "none")
				+ ", output: " + (output != null ? output.Name : "none") + ")";
			return new ComposedDefinition(name, mainRuleSet, baseDefinition);
		}

		/// <summary>
		/// AvalonEdit lets a nested span or rule win a tie against the enclosing span's end, so
		/// C#'s "#" preprocessor span would otherwise swallow the closing "#>" (and highlight the
		/// rest of the line as C#). Returns a copy of the code rule set whose top-level spans and
		/// rules cannot start where the control block ends.
		/// </summary>
		static HighlightingRuleSet GuardEnd(HighlightingRuleSet ruleSet, Regex end)
		{
			var guarded = new HighlightingRuleSet { Name = ruleSet.Name };
			foreach (var span in ruleSet.Spans) {
				guarded.Spans.Add(new HighlightingSpan {
					StartExpression = Guard(span.StartExpression, end),
					EndExpression = span.EndExpression,
					StartColor = span.StartColor,
					SpanColor = span.SpanColor,
					EndColor = span.EndColor,
					SpanColorIncludesStart = span.SpanColorIncludesStart,
					SpanColorIncludesEnd = span.SpanColorIncludesEnd,
					RuleSet = span.RuleSet
				});
			}
			foreach (var rule in ruleSet.Rules)
				guarded.Rules.Add(new HighlightingRule { Regex = Guard(rule.Regex, end), Color = rule.Color });
			return guarded;
		}
		
		static Regex Guard(Regex start, Regex end)
		{
			return new Regex("(?!" + end + ")(?:" + start + ")", start.Options);
		}
		
		static IHighlightingDefinition Find(IEnumerable<T4SyntaxMode> modes, Func<T4SyntaxMode, string[]> keys, string value)
		{
			var mode = modes.FirstOrDefault(m => keys(m).Any(k => k.Equals(value, StringComparison.OrdinalIgnoreCase)));
			return mode != null ? HighlightingManager.Instance.GetDefinition(mode.SyntaxMode) : null;
		}

		sealed class ComposedDefinition : IHighlightingDefinition
		{
			readonly IHighlightingDefinition baseDefinition;

			public ComposedDefinition(string name, HighlightingRuleSet mainRuleSet, IHighlightingDefinition baseDefinition)
			{
				this.Name = name;
				this.MainRuleSet = mainRuleSet;
				this.baseDefinition = baseDefinition;
			}

			public string Name { get; private set; }
			public HighlightingRuleSet MainRuleSet { get; private set; }

			public HighlightingRuleSet GetNamedRuleSet(string name)
			{
				return baseDefinition.GetNamedRuleSet(name);
			}

			public HighlightingColor GetNamedColor(string name)
			{
				return baseDefinition.GetNamedColor(name);
			}

			public IEnumerable<HighlightingColor> NamedHighlightingColors {
				get { return baseDefinition.NamedHighlightingColors; }
			}

			public IDictionary<string, string> Properties {
				get { return baseDefinition.Properties; }
			}
		}
	}
}
