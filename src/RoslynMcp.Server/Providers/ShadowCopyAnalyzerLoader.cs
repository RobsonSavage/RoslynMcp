using System.Collections.Concurrent;
using System.Composition;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Serilog;

namespace RoslynMcp.Server.Providers;

#pragma warning disable CS0618 // IAnalyzerService is obsolete, but it is still MSBuildWorkspace's only loader hook.

/// <summary>
/// Replaces MSBuildWorkspace's DefaultAnalyzerService, whose loader maps analyzer and generator
/// DLLs in place. That holds them in ~/.nuget/packages for the life of the server, so a restore
/// that re-extracts a package fails with access denied while any session is open (bug 4953).
/// </summary>
[ExportWorkspaceService(typeof(IAnalyzerService), ServiceLayer.Host), Shared]
internal sealed class ShadowCopyAnalyzerService : IAnalyzerService
{
    public static HostServices Host { get; } =
        MefHostServices.Create(MSBuildMefHostServices.DefaultAssemblies.Add(typeof(ShadowCopyAnalyzerService).Assembly));

    private readonly ShadowCopyAnalyzerLoader _loader = new(
        Path.Combine(Path.GetTempPath(), "RoslynMcp", "analyzers"), Environment.ProcessId);

    [ImportingConstructor]
    public ShadowCopyAnalyzerService()
    {
    }

    public IAnalyzerAssemblyLoader GetLoader() => _loader;
}

#pragma warning restore CS0618

/// <summary>
/// Copies each analyzer to a per-process folder before loading it, one load context per source
/// directory as Roslyn's own loader does, so two packages can carry the same assembly name.
/// </summary>
internal sealed class ShadowCopyAnalyzerLoader : IAnalyzerAssemblyLoader
{
    private readonly string _shadowRoot;
    private readonly ConcurrentDictionary<string, string> _dependencies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DirectoryContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    public ShadowCopyAnalyzerLoader(string root, int processId)
    {
        _shadowRoot = Path.Combine(root, processId.ToString());
        SweepAbandoned(root, processId);
    }

    public void AddDependencyLocation(string fullPath) =>
        _dependencies.TryAdd(Path.GetFileNameWithoutExtension(fullPath), fullPath);

    public Assembly LoadFromPath(string fullPath)
    {
        AddDependencyLocation(fullPath);
        return ContextFor(Path.GetDirectoryName(fullPath)!).LoadOriginal(fullPath);
    }

    private DirectoryContext ContextFor(string directory) =>
        _contexts.GetOrAdd(directory, d => new DirectoryContext(this, d, Path.Combine(_shadowRoot, Hash(d))));

    private static string Hash(string directory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..16];

    // ponytail: a dead server's folder is deleted only when a later server starts, and a reused pid
    // keeps an abandoned folder alive; record a start time in the folder if the temp space matters.
    private static void SweepAbandoned(string root, int processId)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == processId || IsRunning(pid))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Could not delete abandoned analyzer shadow folder {Folder}", dir);
            }
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class DirectoryContext(ShadowCopyAnalyzerLoader owner, string sourceDirectory, string shadowDirectory)
        : AssemblyLoadContext($"RoslynMcp.Analyzers:{sourceDirectory}")
    {
        private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

        // One lock for the whole loader: Load re-enters it for dependencies, possibly in another
        // directory's context, and a per-context lock could deadlock two threads crossing over.
        public Assembly LoadOriginal(string fullPath)
        {
            lock (owner._contexts)
            {
                if (_loaded.TryGetValue(fullPath, out var loaded))
                    return loaded;
                var shadow = Path.Combine(shadowDirectory, Path.GetRelativePath(sourceDirectory, fullPath));
                Directory.CreateDirectory(Path.GetDirectoryName(shadow)!);
                File.Copy(fullPath, shadow, overwrite: true);
                Log.Debug("Shadow-copied analyzer {Source} to {Shadow}", fullPath, shadow);
                return _loaded[fullPath] = LoadFromAssemblyPath(shadow);
            }
        }

        protected override Assembly? Load(AssemblyName name)
        {
            // Microsoft.CodeAnalysis and the framework must come from the host, or the analyzer's
            // types are foreign to the compiler that loads them.
            try
            {
                return Default.LoadFromAssemblyName(name);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
            {
            }

            var directory = string.IsNullOrEmpty(name.CultureName)
                ? sourceDirectory
                : Path.Combine(sourceDirectory, name.CultureName);
            var sibling = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(sibling))
                return LoadOriginal(sibling);

            return name.Name != null && owner._dependencies.TryGetValue(name.Name, out var dependency)
                ? owner.LoadFromPath(dependency)
                : null;
        }
    }
}
