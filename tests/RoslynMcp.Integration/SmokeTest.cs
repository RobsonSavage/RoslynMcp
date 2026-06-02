using Xunit;

namespace RoslynMcp.Integration;

public class SmokeTest
{
    [Fact]
    public void SharedContracts_RecordEquality()
    {
        // Verify record types work correctly (catch assembly loading issues)
        var loc1 = new RoslynMcp.Shared.Contracts.Common.CodeLocation("test.cs", 0, 0, 1, 0);
        var loc2 = new RoslynMcp.Shared.Contracts.Common.CodeLocation("test.cs", 0, 0, 1, 0);
        Assert.Equal(loc1, loc2);

        var range = new RoslynMcp.Shared.Contracts.Common.CodeRange(0, 0, 10, 5);
        Assert.Equal(0, range.StartLine);
        Assert.Equal(10, range.EndLine);
    }
}
