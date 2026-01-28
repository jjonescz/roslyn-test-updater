using CSharpDiff.Patches;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit.Abstractions;

namespace RoslynTestUpdater.Tests;

public sealed class SnapshotTests : IDisposable
{
    private const string TestOutputFileName = "TestOutput.txt";
    private readonly RedirectOutput redirectOutput;

    public SnapshotTests(ITestOutputHelper output)
    {
        Console.SetOut(redirectOutput = new RedirectOutput(output));
    }

    public void Dispose()
    {
        redirectOutput.Flush();
    }

    [Theory]
    [MemberData(nameof(SnapshotDirs))]
    public void Snapshots(string snapshotDirName)
    {
        var snapshotDirPath = Path.Join(GetTestsDirPath(), snapshotDirName);
        var fileSystem = new TestFileSystem(snapshotDirPath);
        var program = new Program(fileSystem);
        using (var testOutput = new StreamReader(Path.Join(snapshotDirPath, TestOutputFileName)))
        {
            program.Run(testOutput);
        }

        // Remove untouched files.
        foreach (var file in Directory.EnumerateFiles(snapshotDirPath, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (!fileSystem.TouchedFiles.Contains(file) && fileName != TestOutputFileName)
            {
                File.Delete(file);
            }
        }
    }

    public static TheoryData<string> SnapshotDirs => FindSnapshotDirs();

    private static TheoryData<string> FindSnapshotDirs()
    {
        var result = new TheoryData<string>();
        foreach (var snapshotDirPath in Directory.EnumerateDirectories(GetTestsDirPath()))
        {
            result.Add(Path.GetFileName(snapshotDirPath));
        }
        return result;
    }

    private static string GetTestsDirPath([CallerFilePath] string testFilePath = null!)
    {
        return Path.Join(Path.GetDirectoryName(testFilePath), "Snapshots");
    }
}

public class TestFileSystem : IFileSystem
{
    private readonly HashSet<string> touchedFiles = new();

    public TestFileSystem(string snapshotDirPath)
    {
        SnapshotDirPath = snapshotDirPath;
        SnapshotsRootPath = Path.GetDirectoryName(snapshotDirPath)!;
    }

    public string SnapshotDirPath { get; }
    public string SnapshotsRootPath { get; }
    public IReadOnlySet<string> TouchedFiles => touchedFiles;

    /// <summary>
    /// It can be useful to set this to <see langword="false"/> for some test debugging scenarios.
    /// </summary>
    public bool WritePatches { get; init; } = true;

    private string TranslatePath(string path, bool canUseRoot, bool touch = true)
    {
        var translatedPath = TranslatePathCore(path: path, canUseRoot: canUseRoot);
        if (touch)
        {
            touchedFiles.Add(translatedPath);
        }
        return translatedPath;
    }

    private string TranslatePathCore(string path, bool canUseRoot)
    {
        if (canUseRoot && path.StartsWith("C:"))
        {
            return Path.Join(SnapshotsRootPath, Path.GetFileName(path));
        }
        return Path.Join(SnapshotDirPath, Path.GetFileName(path));
    }

    public StreamWriter CreateText(string path)
    {
        return File.CreateText(TranslatePath(path, canUseRoot: false));
    }

    public string GetFullPath(string path)
    {
        return TranslatePath(path, canUseRoot: true);
    }

    public string ReadAllText(string path)
    {
        return File.ReadAllText(TranslatePath(path, canUseRoot: true));
    }

    public void WriteAllText(string path, string? contents, Encoding encoding)
    {
        if (WritePatches)
        {
            var original = ReadAllText(path);
            var patch = new Patch().create(Path.GetFileName(path), original, contents);
            var target = $"{TranslatePath(path, canUseRoot: false, touch: false)}.patch";
            touchedFiles.Add(target);
            File.WriteAllText(target, patch);
        }
        else
        {
            File.WriteAllText(TranslatePath(path, canUseRoot: false), contents, encoding);
        }
    }
}

public class RedirectOutput : TextWriter
{
    private readonly ITestOutputHelper output;
    private readonly StringBuilder buffer;

    public RedirectOutput(ITestOutputHelper output)
    {
        this.output = output;
        buffer = new StringBuilder();
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        buffer.Append(value);
    }

    public override void Flush()
    {
        output.WriteLine(buffer.ToString());
        buffer.Clear();
    }
}
