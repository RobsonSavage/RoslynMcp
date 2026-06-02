using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Common;
using RoslynMcp.Shared.Contracts.Structure;
using System.Xml.Linq;

namespace RoslynMcp.Core.Services;

partial class StructureService
{
    // ── 9. get_xml_documentation ──

    public async Task<Result<XmlDocumentationResponse>> GetXmlDocumentationAsync(
        GetXmlDocumentationRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<XmlDocumentationResponse>.Fail(error);

        var rawXml = symbol!.GetDocumentationCommentXml(cancellationToken: ct);

        if (string.IsNullOrWhiteSpace(rawXml))
        {
            return new XmlDocumentationResponse(RoslynMapper.ToSymbolInfo(symbol!), RawXml: rawXml);
        }

        var parsed = ParseXmlDocumentation(rawXml!);

        return new XmlDocumentationResponse(
            RoslynMapper.ToSymbolInfo(symbol!),
            Summary: parsed.Summary,
            Remarks: parsed.Remarks,
            Returns: parsed.Returns,
            Parameters: parsed.Parameters,
            Exceptions: parsed.Exceptions,
            Example: parsed.Example,
            RawXml: rawXml);
    }

    #region Private Helpers

    private ParsedDocumentation ParseXmlDocumentation(string rawXml)
    {
        try
        {
            var doc = XDocument.Parse(rawXml);
            var member = doc.Root;
            if (member is null) return default;

            // The root might be <member> or directly contain the elements
            var elements = member.Name.LocalName == "member"
                ? member.Elements()
                : doc.Root!.Elements();

            string? summary = null, remarks = null, returns = null, example = null;
            var parameters = new List<ParameterDocumentation>();
            var exceptions = new List<ExceptionDocumentation>();

            foreach (var el in elements)
            {
                switch (el.Name.LocalName)
                {
                    case "summary":
                        summary = GetInnerText(el);
                        break;
                    case "remarks":
                        remarks = GetInnerText(el);
                        break;
                    case "returns":
                        returns = GetInnerText(el);
                        break;
                    case "example":
                        example = GetInnerText(el);
                        break;
                    case "param":
                        var paramName = el.Attribute("name")?.Value ?? "";
                        parameters.Add(new ParameterDocumentation(paramName, GetInnerText(el)));
                        break;
                    case "exception":
                        var cref = el.Attribute("cref")?.Value ?? "";
                        // Strip "T:" prefix from cref
                        if (cref.StartsWith("T:"))
                            cref = cref.Substring(2);
                        exceptions.Add(new ExceptionDocumentation(cref, GetInnerText(el)));
                        break;
                }
            }

            return new ParsedDocumentation(
                summary, remarks, returns, example,
                parameters.Count > 0 ? parameters : null,
                exceptions.Count > 0 ? exceptions : null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning("Failed to parse XML documentation: {Error}", ex.Message);
            return default;
        }
    }

    private static string GetInnerText(XElement element)
    {
        // Get text content, collapsing whitespace
        var text = string.Concat(element.Nodes().Select(n => n is XElement e
            ? e.Name.LocalName == "see"
                ? e.Attribute("cref")?.Value?.Replace("T:", "").Replace("M:", "").Replace("P:", "") ?? ""
                : e.Value
            : n.ToString()));

        // Normalize whitespace
        return string.Join(" ", text.Split(new[] { ' ', '\r', '\n', '\t' },
            StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private readonly record struct ParsedDocumentation(
        string? Summary,
        string? Remarks,
        string? Returns,
        string? Example,
        IReadOnlyList<ParameterDocumentation>? Parameters,
        IReadOnlyList<ExceptionDocumentation>? Exceptions);

    #endregion
}
