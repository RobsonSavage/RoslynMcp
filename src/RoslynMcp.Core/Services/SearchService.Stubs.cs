using Microsoft.CodeAnalysis;
using RoslynMcp.Shared.Contracts.Common;

using SymbolInfo = RoslynMcp.Shared.Contracts.Common.SymbolInfo;

namespace RoslynMcp.Core.Services;

public partial class SearchService
{
    internal readonly struct ReferenceStub
    {
        public readonly CodeLocation Location;
        public readonly DocumentId? DocumentId;
        public readonly int SourceSpanStart;

        public ReferenceStub(CodeLocation location, DocumentId? documentId, int sourceSpanStart)
        {
            Location = location;
            DocumentId = documentId;
            SourceSpanStart = sourceSpanStart;
        }
    }

    internal readonly struct ImplementationStub
    {
        public readonly SymbolInfo Symbol;
        public readonly CodeLocation Location;
        public readonly string? FilePath;
        public readonly int StartLine;

        public ImplementationStub(SymbolInfo symbol, CodeLocation location, string? filePath, int startLine)
        {
            Symbol = symbol;
            Location = location;
            FilePath = filePath;
            StartLine = startLine;
        }
    }

    internal readonly struct CallerStub
    {
        public readonly SymbolInfo CallingSymbol;
        public readonly CodeLocation Location;
        public readonly bool IsDirect;
        public readonly string? FilePath;
        public readonly int StartLine;

        public CallerStub(SymbolInfo callingSymbol, CodeLocation location, bool isDirect, string? filePath, int startLine)
        {
            CallingSymbol = callingSymbol;
            Location = location;
            IsDirect = isDirect;
            FilePath = filePath;
            StartLine = startLine;
        }
    }

    internal readonly struct OverrideStub
    {
        public readonly SymbolInfo Symbol;
        public readonly CodeLocation Location;
        public readonly string? ContainingType;
        public readonly string? FilePath;
        public readonly int StartLine;

        public OverrideStub(SymbolInfo symbol, CodeLocation location, string? containingType, string? filePath, int startLine)
        {
            Symbol = symbol;
            Location = location;
            ContainingType = containingType;
            FilePath = filePath;
            StartLine = startLine;
        }
    }

    internal readonly struct DerivedTypeStub
    {
        public readonly SymbolInfo Symbol;
        public readonly CodeLocation Location;
        public readonly bool IsDirect;
        public readonly string? FilePath;
        public readonly int StartLine;

        public DerivedTypeStub(SymbolInfo symbol, CodeLocation location, bool isDirect, string? filePath, int startLine)
        {
            Symbol = symbol;
            Location = location;
            IsDirect = isDirect;
            FilePath = filePath;
            StartLine = startLine;
        }
    }

    internal readonly struct AttributeUsageStub
    {
        public readonly CodeLocation Location;
        public readonly DocumentId? DocumentId;
        public readonly int SourceSpanStart;

        public AttributeUsageStub(CodeLocation location, DocumentId? documentId, int sourceSpanStart)
        {
            Location = location;
            DocumentId = documentId;
            SourceSpanStart = sourceSpanStart;
        }
    }

    internal readonly struct EventSubscriberStub
    {
        public readonly CodeLocation Location;
        public readonly DocumentId? DocumentId;
        public readonly int SourceSpanStart;
        public readonly string SubscriptionKind;
        public readonly int StartLine;

        public EventSubscriberStub(CodeLocation location, DocumentId? documentId, int sourceSpanStart, string subscriptionKind, int startLine)
        {
            Location = location;
            DocumentId = documentId;
            SourceSpanStart = sourceSpanStart;
            SubscriptionKind = subscriptionKind;
            StartLine = startLine;
        }
    }

    internal readonly struct CalleeStub
    {
        public readonly SymbolInfo CalleeSymbol;
        public readonly CodeLocation Location;
        public readonly DocumentId? DocumentId;
        public readonly int StartLine;

        public CalleeStub(SymbolInfo calleeSymbol, CodeLocation location, DocumentId? documentId, int startLine)
        {
            CalleeSymbol = calleeSymbol;
            Location = location;
            DocumentId = documentId;
            StartLine = startLine;
        }
    }

    internal readonly struct TestClassStub
    {
        public readonly SymbolInfo Symbol;
        public readonly CodeLocation Location;
        public readonly DocumentId DocumentId;
        public readonly string MetadataName;

        public TestClassStub(SymbolInfo symbol, CodeLocation location, DocumentId documentId, string metadataName)
        {
            Symbol = symbol;
            Location = location;
            DocumentId = documentId;
            MetadataName = metadataName;
        }
    }

    internal readonly struct ExtensionMethodStub
    {
        public readonly CodeLocation Location;
        public readonly DocumentId DocumentId;
        public readonly string ContainingTypeMetadataName;
        public readonly string MethodName;
        public readonly int Arity;
        public readonly int ParameterCount;

        public ExtensionMethodStub(CodeLocation location, DocumentId documentId,
            string containingTypeMetadataName, string methodName, int arity, int parameterCount)
        {
            Location = location;
            DocumentId = documentId;
            ContainingTypeMetadataName = containingTypeMetadataName;
            MethodName = methodName;
            Arity = arity;
            ParameterCount = parameterCount;
        }
    }
}
