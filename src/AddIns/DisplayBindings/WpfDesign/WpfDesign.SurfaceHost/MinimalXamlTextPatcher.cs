using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

using ICSharpCode.WpfDesign.XamlDom;

namespace ICSharpCode.WpfDesign.SurfaceHost
{
	/// <summary>
	/// Writes the design model back as a patch over the text it was parsed from, instead of
	/// regenerating the whole document. Regenerating (<c>XmlWriter { Indent = true }</c>) collapsed
	/// every multi-line start tag, moved attributes, dropped the trailing newline and changed
	/// ~35 lines for a one-attribute edit (designer-common.md, "Document ownership modes").
	///
	/// Identity comes from <see cref="PositionXmlElement"/>: every element the parser loaded keeps
	/// the line/column of its name in the parsed text, so a model element is matched to its original
	/// span without guessing. Elements the designer created carry no line info and are written
	/// fresh; an element moved to another parent is written fresh at its new place and removed from
	/// the old one. Only what changed is touched: an attribute value, an added or removed attribute,
	/// an inserted or removed child. A shape this patcher cannot express (mixed text content that
	/// changed, a scan that disagrees with the XML parser) returns null so the caller can fall back
	/// to a full save rather than write something wrong.
	/// </summary>
	static class MinimalXamlTextPatcher
	{
		public static string? TryPatch(string original, XmlDocument current)
		{
			try
			{
				return new Patcher(original).Run(current);
			}
			catch (PatchNotExpressibleException e)
			{
				Console.Error.WriteLine($"design-host: minimal save not expressible ({e.Message}); regenerating the document.");
				return null;
			}
			catch (XmlException e)
			{
				Console.Error.WriteLine($"design-host: minimal save could not re-read the source ({e.Message}); regenerating the document.");
				return null;
			}
		}

		sealed class PatchNotExpressibleException : Exception
		{
			public PatchNotExpressibleException(string message) : base(message) { }
		}

		sealed class SrcAttr
		{
			public string Name = "";
			public int Start;       // start of the whitespace before the name
			public int NameStart;
			public int ValueStart;  // after the opening quote
			public int ValueEnd;    // at the closing quote
			public char Quote;
			public int End;         // after the closing quote
		}

		sealed class SrcElement
		{
			public string Name = "";
			public int Start;        // the '<'
			public int NameEnd;
			public int CloseStart;   // the '/' of "/>" or the '>' of the start tag
			public int StartTagEnd;  // after the start tag's '>'
			public bool SelfClosing;
			public int ContentStart = -1;
			public int ContentEnd = -1;  // the '<' of the end tag
			public int End;
			public readonly List<SrcAttr> Attributes = new List<SrcAttr>();
			/// <summary>Child elements, comments and non-blank text runs, in document order.</summary>
			public readonly List<SrcNode> Children = new List<SrcNode>();
		}

		enum SrcNodeKind { Element, Comment, Text, CData }

		sealed record SrcNode(SrcNodeKind Kind, int Start, int End, SrcElement? Element = null);

		readonly record struct Edit(int Start, int End, string Text, int Sequence);

		sealed class Patcher
		{
			readonly string text;
			readonly string newline;
			readonly string indentUnit;
			readonly List<int> lineStarts = new List<int> { 0 };
			readonly Dictionary<(int Line, int Column), SrcElement> sourceByKey = new Dictionary<(int, int), SrcElement>();
			readonly Dictionary<(int Line, int Column), XmlElement> baseByKey = new Dictionary<(int, int), XmlElement>();
			readonly List<Edit> edits = new List<Edit>();
			SrcElement? sourceRoot;

			public Patcher(string original)
			{
				text = original;
				newline = original.Contains("\r\n") ? "\r\n" : "\n";
				for (var i = 0; i < text.Length; i++)
				{
					if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
						lineStarts.Add(i + 1);
				}
				Scan();
				indentUnit = DetectIndentUnit();
			}

			public string Run(XmlDocument current)
			{
				// The baseline is the parsed text read again the same way the design context read it,
				// so its line/column keys are the ones the model's elements still carry.
				var baseline = new PositionXmlDocument();
				using (var reader = XmlReader.Create(new StringReader(text)))
					baseline.Load(reader);
				foreach (var element in baseline.SelectNodes("//*")!.Cast<XmlElement>())
				{
					var key = KeyOf(element) ?? throw new PatchNotExpressibleException("baseline element without line info");
					if (!sourceByKey.TryGetValue(key, out var source) || source.Name != element.Name)
						throw new PatchNotExpressibleException($"source scan disagrees with the parser at {key.Line}:{key.Column}");
					baseByKey[key] = element;
				}

				var root = current.DocumentElement ?? throw new PatchNotExpressibleException("no document element");
				var baseRoot = baseline.DocumentElement!;
				if (sourceRoot == null || KeyOf(root) != KeyOf(baseRoot) || root.Name != baseRoot.Name)
					throw new PatchNotExpressibleException("the root element was replaced");
				PatchElement(root, baseRoot, sourceRoot);

				var result = new StringBuilder(text);
				foreach (var edit in edits
					.OrderByDescending(e => e.Start)
					.ThenByDescending(e => e.End > e.Start)   // at one offset, remove before inserting
					.ThenByDescending(e => e.Sequence))
				{
					result.Remove(edit.Start, edit.End - edit.Start);
					result.Insert(edit.Start, edit.Text);
				}
				return result.ToString();
			}

			void AddEdit(int start, int end, string replacement) =>
				edits.Add(new Edit(start, end, replacement, edits.Count));

			void PatchElement(XmlElement element, XmlElement baseElement, SrcElement source)
			{
				PatchAttributes(element, source);
				PatchChildren(element, baseElement, source);
			}

			void PatchAttributes(XmlElement element, SrcElement source)
			{
				var remaining = element.Attributes.Cast<XmlAttribute>().ToDictionary(a => a.Name, StringComparer.Ordinal);
				foreach (var attribute in source.Attributes)
				{
					if (!remaining.Remove(attribute.Name, out var now))
					{
						AddEdit(attribute.Start, attribute.End, "");
						continue;
					}
					var value = DecodeAttribute(attribute);
					if (value != now.Value)
						AddEdit(attribute.ValueStart, attribute.ValueEnd, EscapeAttribute(now.Value, attribute.Quote));
				}
				if (remaining.Count == 0)
					return;

				var last = source.Attributes.LastOrDefault();
				var insertAt = last?.End ?? source.NameEnd;
				// Match the file's layout: one attribute per line when the last one sits on its own line.
				var separator = last != null && text.AsSpan(last.Start, last.NameStart - last.Start).IndexOfAny('\r', '\n') >= 0
					? text.Substring(last.Start, last.NameStart - last.Start)
					: " ";
				var added = new StringBuilder();
				foreach (var attribute in element.Attributes.Cast<XmlAttribute>().Where(a => remaining.ContainsKey(a.Name)))
					added.Append(separator).Append(attribute.Name).Append("=\"").Append(EscapeAttribute(attribute.Value, '"')).Append('"');
				AddEdit(insertAt, insertAt, added.ToString());
			}

			void PatchChildren(XmlElement element, XmlElement baseElement, SrcElement source)
			{
				var baseChildren = SignificantChildren(baseElement);
				var children = SignificantChildren(element);
				if (baseChildren.Count != source.Children.Count)
					throw new PatchNotExpressibleException($"child scan disagrees with the parser under <{source.Name}>");
				for (var i = 0; i < baseChildren.Count; i++)
				{
					if (KindOf(baseChildren[i]) != source.Children[i].Kind)
						throw new PatchNotExpressibleException($"child kind disagrees with the parser under <{source.Name}>");
				}

				if (baseChildren.Count == 0)
				{
					if (children.Count > 0)
						FillEmptyElement(element, source, children);
					return;
				}

				// Longest common subsequence: whatever keeps its relative order stays where it is in
				// the text; everything else is removed from its old place and written at its new one.
				var lcs = new int[baseChildren.Count + 1, children.Count + 1];
				for (var i = baseChildren.Count - 1; i >= 0; i--)
					for (var j = children.Count - 1; j >= 0; j--)
						lcs[i, j] = Same(baseChildren[i], children[j], baseElement)
							? lcs[i + 1, j + 1] + 1
							: Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

				var firstLine = source.Children.FirstOrDefault(node => node.Kind is SrcNodeKind.Element or SrcNodeKind.Comment);
				var childIndent = firstLine != null ? LineIndent(firstLine.Start) : LineIndent(source.Start) + indentUnit;
				var anchor = source.ContentStart;
				int bi = 0, ci = 0;
				while (bi < baseChildren.Count || ci < children.Count)
				{
					if (bi < baseChildren.Count && ci < children.Count && Same(baseChildren[bi], children[ci], baseElement))
					{
						var childSource = source.Children[bi];
						if (children[ci] is XmlElement child)
							PatchElement(child, (XmlElement)baseChildren[bi], childSource.Element!);
						anchor = childSource.End;
						bi++;
						ci++;
					}
					else if (ci < children.Count && (bi >= baseChildren.Count || lcs[bi, ci + 1] >= lcs[bi + 1, ci]))
					{
						AddEdit(anchor, anchor, children[ci] is XmlText or XmlCDataSection
							? SerializeNode(children[ci], childIndent)
							: newline + childIndent + SerializeNode(children[ci], childIndent));
						ci++;
					}
					else
					{
						var childSource = source.Children[bi];
						AddEdit(childSource.Kind is SrcNodeKind.Text or SrcNodeKind.CData
							? childSource.Start
							: StartIncludingLeadingLineBreak(childSource.Start), childSource.End, "");
						bi++;
					}
				}
			}

			bool Same(XmlNode baseNode, XmlNode node, XmlElement baseParent) =>
				baseNode is XmlElement baseElement
					? node is XmlElement element && MatchKey(element, baseParent) == KeyOf(baseElement)
					: node is not XmlElement && KindOf(node) == KindOf(baseNode) && node.Value == baseNode.Value;

			static SrcNodeKind KindOf(XmlNode node) => node.NodeType switch {
				XmlNodeType.Element => SrcNodeKind.Element,
				XmlNodeType.Comment => SrcNodeKind.Comment,
				XmlNodeType.Text => SrcNodeKind.Text,
				XmlNodeType.CDATA => SrcNodeKind.CData,
				_ => throw new PatchNotExpressibleException($"unsupported {node.NodeType} node")
			};

			void FillEmptyElement(XmlElement element, SrcElement source, List<XmlNode> children)
			{
				var indent = LineIndent(source.Start);
				var body = new StringBuilder();
				if (children.All(node => node is XmlText or XmlCDataSection))
				{
					foreach (var node in children)
						body.Append(EscapeText(node.Value ?? ""));
				}
				else
				{
					foreach (var child in children)
						body.Append(newline).Append(indent).Append(indentUnit).Append(SerializeNode(child, indent + indentUnit));
					body.Append(newline).Append(indent);
				}
				if (source.SelfClosing)
				{
					var start = source.CloseStart;
					while (start > source.NameEnd && char.IsWhiteSpace(text[start - 1]))
						start--;
					AddEdit(start, source.StartTagEnd, ">" + body + "</" + element.Name + ">");
				}
				else
				{
					AddEdit(source.ContentStart, source.ContentEnd, body.ToString());
				}
			}

			/// <summary>The key a model element is matched by: its original position, but only when
			/// it still sits under the same parent it was parsed under.</summary>
			(int, int)? MatchKey(XmlElement element, XmlNode baseParent)
			{
				var key = KeyOf(element);
				if (key == null || !baseByKey.TryGetValue(key.Value, out var baseElement))
					return null;
				return baseElement.ParentNode == baseParent && baseElement.Name == element.Name ? key : null;
			}

			static (int Line, int Column)? KeyOf(XmlElement element) =>
				element is PositionXmlElement positioned && positioned.HasLineInfo()
					? (positioned.LineNumber, positioned.LinePosition)
					: null;

			static List<XmlNode> SignificantChildren(XmlElement element) =>
				element.ChildNodes.Cast<XmlNode>()
					.Where(node => node.NodeType != XmlNodeType.Whitespace && node.NodeType != XmlNodeType.SignificantWhitespace)
					.ToList();

			string Serialize(XmlElement element, string indent)
			{
				var builder = new StringBuilder();
				builder.Append('<').Append(element.Name);
				foreach (XmlAttribute attribute in element.Attributes)
					builder.Append(' ').Append(attribute.Name).Append("=\"").Append(EscapeAttribute(attribute.Value, '"')).Append('"');
				var children = SignificantChildren(element);
				if (children.Count == 0)
					return builder.Append(" />").ToString();
				builder.Append('>');
				if (children.All(node => node is XmlText || node is XmlCDataSection))
				{
					foreach (var node in children)
						builder.Append(EscapeText(node.Value ?? ""));
				}
				else
				{
					var inner = indent + indentUnit;
					foreach (var node in children)
						builder.Append(newline).Append(inner).Append(SerializeNode(node, inner));
					builder.Append(newline).Append(indent);
				}
				return builder.Append("</").Append(element.Name).Append('>').ToString();
			}

			string SerializeNode(XmlNode node, string indent) => node switch {
				XmlElement element => Serialize(element, indent),
				XmlComment comment => "<!--" + comment.Value + "-->",
				_ => EscapeText(node.Value ?? "")
			};

			static string EscapeAttribute(string value, char quote)
			{
				var builder = new StringBuilder(value.Length);
				foreach (var c in value)
				{
					switch (c)
					{
						case '&': builder.Append("&amp;"); break;
						case '<': builder.Append("&lt;"); break;
						case '"' when quote == '"': builder.Append("&quot;"); break;
						case '\'' when quote == '\'': builder.Append("&apos;"); break;
						case '\n': builder.Append("&#xA;"); break;
						case '\r': builder.Append("&#xD;"); break;
						case '\t': builder.Append("&#x9;"); break;
						default: builder.Append(c); break;
					}
				}
				return builder.ToString();
			}

			static string EscapeText(string value) =>
				value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

			string DecodeAttribute(SrcAttr attribute)
			{
				// Let the XML reader do entity and whitespace normalisation, so an untouched value that
				// uses &quot; or &#xA; compares equal to what the model holds.
				var raw = text.Substring(attribute.ValueStart, attribute.ValueEnd - attribute.ValueStart);
				using var reader = XmlReader.Create(new StringReader("<a v=" + attribute.Quote + raw + attribute.Quote + "/>"));
				reader.MoveToContent();
				return reader.GetAttribute("v") ?? raw;
			}

			/// <summary>Where removing an element should start so its own line goes with it.</summary>
			int StartIncludingLeadingLineBreak(int start)
			{
				var i = start;
				while (i > 0 && (text[i - 1] == ' ' || text[i - 1] == '\t'))
					i--;
				if (i > 0 && text[i - 1] == '\n')
				{
					i--;
					if (i > 0 && text[i - 1] == '\r')
						i--;
					return i;
				}
				return start;
			}

			string LineIndent(int offset)
			{
				var lineStart = offset;
				while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\r')
					lineStart--;
				var end = lineStart;
				while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
					end++;
				return text.Substring(lineStart, end - lineStart);
			}

			string DetectIndentUnit()
			{
				foreach (var source in sourceByKey.Values.OrderBy(s => s.Start))
				{
					var indent = LineIndent(source.Start);
					if (indent.Length > 0)
						return indent[0] == '\t' ? "\t" : new string(' ', Math.Min(indent.Length, 4));
				}
				return "    ";
			}

			(int Line, int Column) Position(int offset)
			{
				var index = lineStarts.BinarySearch(offset);
				if (index < 0)
					index = ~index - 1;
				return (index + 1, offset - lineStarts[index] + 1);
			}

			void Scan()
			{
				var stack = new Stack<SrcElement>();
				var i = 0;
				while (i < text.Length)
				{
					if (text[i] != '<')
					{
						var runStart = i;
						while (i < text.Length && text[i] != '<')
							i++;
						if (stack.Count > 0 && text.AsSpan(runStart, i - runStart).Trim().Length > 0)
							stack.Peek().Children.Add(new SrcNode(SrcNodeKind.Text, runStart, i));
						continue;
					}
					var start = i;
					if (At(i, "<!--"))
					{
						i = Skip(i, "-->");
						if (stack.Count > 0)
							stack.Peek().Children.Add(new SrcNode(SrcNodeKind.Comment, start, i));
					}
					else if (At(i, "<![CDATA["))
					{
						i = Skip(i, "]]>");
						if (stack.Count > 0)
							stack.Peek().Children.Add(new SrcNode(SrcNodeKind.CData, start, i));
					}
					else if (At(i, "<?"))
					{
						if (stack.Count > 0)
							throw new PatchNotExpressibleException("processing instruction inside an element");
						i = Skip(i, "?>");
					}
					else if (At(i, "<!"))
						i = Skip(i, ">");
					else if (At(i, "</"))
					{
						if (stack.Count == 0)
							throw new PatchNotExpressibleException("unbalanced end tag");
						var open = stack.Pop();
						open.ContentEnd = i;
						i = Skip(i, ">");
						open.End = i;
					}
					else
					{
						var element = ScanStartTag(i);
						sourceByKey[Position(element.Start + 1)] = element;
						sourceRoot ??= element;
						if (stack.Count > 0)
							stack.Peek().Children.Add(new SrcNode(SrcNodeKind.Element, element.Start, -1, element));
						if (!element.SelfClosing)
							stack.Push(element);
						i = element.StartTagEnd;
					}
				}
				if (stack.Count != 0)
					throw new PatchNotExpressibleException("unclosed element");
				// Element child nodes were recorded before their end was known.
				foreach (var element in sourceByKey.Values)
				{
					for (var c = 0; c < element.Children.Count; c++)
					{
						if (element.Children[c].Element is { } child)
							element.Children[c] = element.Children[c] with { End = child.End };
					}
				}
			}

			SrcElement ScanStartTag(int start)
			{
				var element = new SrcElement { Start = start };
				var i = start + 1;
				while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '/' && text[i] != '>')
					i++;
				element.Name = text.Substring(start + 1, i - start - 1);
				element.NameEnd = i;
				while (true)
				{
					var whitespaceStart = i;
					while (i < text.Length && char.IsWhiteSpace(text[i]))
						i++;
					if (i >= text.Length)
						throw new PatchNotExpressibleException("unterminated start tag");
					if (text[i] == '/' || text[i] == '>')
					{
						element.CloseStart = i;
						element.SelfClosing = text[i] == '/';
						i = Skip(i, ">");
						element.StartTagEnd = i;
						if (!element.SelfClosing)
							element.ContentStart = i;
						else
							element.End = i;
						return element;
					}
					var attribute = new SrcAttr { Start = whitespaceStart, NameStart = i };
					while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '=')
						i++;
					attribute.Name = text.Substring(attribute.NameStart, i - attribute.NameStart);
					while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '='))
						i++;
					if (i >= text.Length || (text[i] != '"' && text[i] != '\''))
						throw new PatchNotExpressibleException("unquoted attribute value");
					attribute.Quote = text[i];
					attribute.ValueStart = i + 1;
					var close = text.IndexOf(attribute.Quote, attribute.ValueStart);
					if (close < 0)
						throw new PatchNotExpressibleException("unterminated attribute value");
					attribute.ValueEnd = close;
					attribute.End = close + 1;
					element.Attributes.Add(attribute);
					i = attribute.End;
				}
			}

			bool At(int i, string token) => string.CompareOrdinal(text, i, token, 0, token.Length) == 0;

			int Skip(int i, string terminator)
			{
				var end = text.IndexOf(terminator, i, StringComparison.Ordinal);
				if (end < 0)
					throw new PatchNotExpressibleException("unterminated markup");
				return end + terminator.Length;
			}
		}
	}
}
