using RoslynMcp.Shared;
using Xunit;

namespace RoslynMcp.Core.Tests.Helpers;

public class PathValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmpty_ReturnsError(string? input)
    {
        var result = PathValidator.Canonicalize(input!, @"C:\test");

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_PATH", result.Error!.ErrorCode);
    }

    [Fact]
    public void RelativePath_Canonicalizes()
    {
        var result = PathValidator.Canonicalize("./foo/bar.cs");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(Path.IsPathRooted(result.Value));
    }

    [Fact]
    public void TraversalBlocked_WhenBoundarySet()
    {
        var result = PathValidator.Canonicalize(@"C:\test\..\evil.cs", @"C:\test");

        Assert.False(result.IsSuccess);
        Assert.Equal("PATH_OUTSIDE_SOLUTION", result.Error!.ErrorCode);
    }

    [Fact]
    public void InsideBoundary_Succeeds()
    {
        var result = PathValidator.Canonicalize(@"C:\test\proj\file.cs", @"C:\test");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(@"C:\test\proj\file.cs", result.Value);
    }

    [Fact]
    public void ExactBoundary_Succeeds()
    {
        var result = PathValidator.Canonicalize(@"C:\test", @"C:\test");

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public void NoBoundary_AllowsAnyPath()
    {
        var result = PathValidator.Canonicalize(@"D:\other\file.cs");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(@"D:\other\file.cs", result.Value);
    }

    [Fact]
    public void InvalidChars_ReturnsError()
    {
        var result = PathValidator.Canonicalize("C:\\test\\file\0.cs");

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_PATH", result.Error!.ErrorCode);
    }

    [Fact]
    public void UncPath_OutsideBoundary()
    {
        var result = PathValidator.Canonicalize(@"\\server\share\file.cs", @"C:\test");

        Assert.False(result.IsSuccess);
        Assert.Equal("PATH_OUTSIDE_SOLUTION", result.Error!.ErrorCode);
    }

    [Fact]
    public void MixedSeparators_Canonicalizes()
    {
        var result = PathValidator.Canonicalize(@"C:/test/proj/file.cs", @"C:\test");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(@"C:\test\proj\file.cs", result.Value);
    }

    [Fact]
    public void TrailingSeparator_BoundaryWorks()
    {
        var result = PathValidator.Canonicalize(@"C:\test\proj\file.cs", @"C:\test\");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(@"C:\test\proj\file.cs", result.Value);
    }
}
