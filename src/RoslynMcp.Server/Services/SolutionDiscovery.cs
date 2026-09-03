using Serilog;

namespace RoslynMcp.Server.Services;

internal static class SolutionDiscovery
{
    // Resolve a codebase from a directory by walking to its enclosing git root, then searching
    // for the nearest solution in that repository. Refusing to scan outside the repository keeps
    // a container directory from selecting an unrelated sibling solution.
    public static string? Discover(string startDir, ILogger log)
    {
        string? gitRoot = null;
        var current = Path.GetFullPath(startDir);
        while (current != null)
        {
            var gitMarker = Path.Combine(current, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
            {
                gitRoot = current;
                log.Information("Found git root: {GitRoot}", gitRoot);
                break;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent == null || parent == current) break;
            current = parent;
        }

        if (gitRoot == null)
        {
            log.Warning(
                "Directory is not inside a git repository: {StartDir}. Refusing to scan unrelated directories",
                startDir);
            return null;
        }

        // On Windows, the legacy wildcard rules can make *.sln match *.slnx, so filter the exact
        // extension after enumeration. The shallowest solution is normally the repository entry point.
        var solution = Directory.EnumerateFiles(gitRoot, "*.sln*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Count(character =>
                character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar))
            .ThenBy(file => file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();

        if (solution != null)
            log.Information("Auto-discovered solution: {SolutionPath}", solution);
        else
            log.Warning("No .sln or .slnx found under git root: {GitRoot}", gitRoot);

        return solution;
    }
}
