#nullable enable
using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Templates
{
    /// <summary>End-user-facing definition of an option exposed by a dotnet template.</summary>
    public sealed record TemplateParameterSummary(
        string Name,
        string DisplayName,
        string? Description,
        string DataType,
        string? DefaultValue,
        bool IsName,
        IReadOnlyDictionary<string, string>? Choices = null);

    /// <summary>
    /// Presentation-shaped template listing entry — decoupled from
    /// <c>Microsoft.TemplateEngine.Abstractions.ITemplateInfo</c> the same way
    /// <c>NuGetSearchResult</c> decouples from <c>NuGet.Protocol</c>'s search metadata
    /// (externals/OpenDevelop/doc/technotes/nuget-manager.md), so listing/filtering logic is testable without a real
    /// installed template package.
    /// </summary>
    public sealed record TemplateSummary(
        string Identity,
        string ShortName,
        string Name,
        string? Description,
        IReadOnlyDictionary<string, string> Tags,
        string? GroupIdentity = null,
        IReadOnlyList<string>? Classifications = null,
        IReadOnlyList<TemplateParameterSummary>? Parameters = null)
    {
        /// <summary>Language supplied by the template engine, shown when template names collide.</summary>
        public string? Language => Tags.TryGetValue("language", out var language) ? language : null;

        public string DisplayName => string.IsNullOrWhiteSpace(Language) ? Name : $"{Name} ({Language})";

        public IReadOnlyList<string> Categories => Classifications ?? Array.Empty<string>();

        public IReadOnlyList<TemplateParameterSummary> TemplateParameters => Parameters ?? Array.Empty<TemplateParameterSummary>();
    }
}
