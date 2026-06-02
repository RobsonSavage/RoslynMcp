using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Search;
using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace RoslynMcp.Core.Services;

public partial class SearchService
{
    // ── 14. text_search ──

    /// <summary>
    /// Cache for compiled glob-to-regex patterns. Keyed by the original FilePattern string.
    /// Glob regexes are simple patterns (e.g. "*.cs") that benefit from reuse across calls.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> s_globRegexCache = new(StringComparer.Ordinal);

    public async Task<Result<TextSearchResponse>> TextSearchAsync(
        TextSearchRequest request, CancellationToken ct = default)
    {
        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("{MethodName}: No solution loaded", nameof(TextSearchAsync));
            return Result<TextSearchResponse>.Fail("No solution loaded");
        }

        if (string.IsNullOrEmpty(request.Pattern))
            return Result<TextSearchResponse>.Fail("Pattern cannot be empty");
        Regex? regex = null;

        if (request.IsRegex)
        {
            try
            {
                var options = request.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(request.Pattern, options, TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException ex)
            {
                return Result<TextSearchResponse>.Fail($"Invalid regex pattern: {ex.Message}");
            }
        }

        var items = new List<TextSearchMatch>();

        Regex? globRegex = null;
        if (request.FilePattern != null)
        {
            globRegex = s_globRegexCache.GetOrAdd(request.FilePattern, static pattern =>
            {
                // Glob-to-regex conversion: supports * (any chars) and ? (single char).
                // NOTE: ** (recursive directory wildcard) is NOT specially handled — it becomes
                // ".*.*" which is functionally equivalent to ".*" for matching purposes.
                // This is acceptable because the glob is only matched against Path.GetFileName()
                // (basename only), where ** is semantically irrelevant.
                var regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            });
        }

        foreach (var project in solution.Projects)
        {
            if (request.ProjectName != null &&
                !string.Equals(project.Name, request.ProjectName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var doc in project.Documents)
            {
                if (items.Count >= PagingHelper.MaxResults) break;
                ct.ThrowIfCancellationRequested();

                if (globRegex != null && doc.FilePath != null)
                {
                    var fileName = Path.GetFileName(doc.FilePath);
                    if (!globRegex.IsMatch(fileName))
                        continue;
                }

                var text = await doc.GetTextAsync(ct).ConfigureAwait(false);

                // Skip files larger than 1MB to avoid LOH pressure
                if (text.Length > 1_048_576)
                {
                    _logger.Warning("text_search: Skipping large file ({Size} chars): {File}", text.Length, doc.FilePath);
                    continue;
                }

                if (regex != null)
                {
                    // Line-by-line regex matching to avoid materializing the entire SourceText
                    // as a single string (text.ToString()), which causes LOH pressure for files >~43KB.
                    // Trade-off: multi-line regex patterns (e.g. "foo\nbar") will NOT match across
                    // line boundaries. This is acceptable for the common case of single-line searches.
                    try
                    {
                        foreach (var textLine in text.Lines)
                        {
                            if (items.Count >= PagingHelper.MaxResults) break;
                            var lineText = textLine.ToString();
                            foreach (Match match in regex.Matches(lineText))
                            {
                                if (items.Count >= PagingHelper.MaxResults) break;
                                var contextLine = RoslynMapper.GetContextLine(text, textLine.LineNumber);
                                items.Add(new TextSearchMatch(
                                    doc.FilePath ?? "", textLine.LineNumber, match.Index,
                                    match.Value, contextLine));
                            }
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        _logger.Warning("Regex timeout on document {File}", doc.FilePath);
                        continue;
                    }
                }
                else
                {
                    var comparison = request.CaseSensitive
                        ? StringComparison.Ordinal
                        : StringComparison.OrdinalIgnoreCase;
                    foreach (var textLine in text.Lines)
                    {
                        if (items.Count >= PagingHelper.MaxResults) break;
                        var lineText = textLine.ToString();
                        int idx = 0;
                        while ((idx = lineText.IndexOf(request.Pattern, idx, comparison)) >= 0)
                        {
                            items.Add(new TextSearchMatch(
                                doc.FilePath ?? "", textLine.LineNumber, idx,
                                request.Pattern, lineText.Trim()));
                            if (items.Count >= PagingHelper.MaxResults) break;
                            idx += request.Pattern.Length;
                        }
                    }
                }
            }
            if (items.Count >= PagingHelper.MaxResults) break;
        }

        return new TextSearchResponse(
            request.Pattern,
            PagingHelper.Page(items, request.Page, request.PageSize));
    }

}
