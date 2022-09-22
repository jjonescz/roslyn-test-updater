using CSharpDiff.Patches;
using System.Runtime.CompilerServices;
using System.Text;

namespace RoslynTestUpdater.Tests;

public class IntegrationTests
{
    [Theory]
    [MemberData(nameof(SnapshotDirs))]
    public void Snapshots(string snapshotDirPath)
    {
        using var testOutput = new StreamReader(Path.Join(snapshotDirPath, "TestOutput.txt"));
        var fileSystem = new TestFileSystem(snapshotDirPath);
        var program = new Program(fileSystem);
        program.Run(testOutput);
    }

    public static TheoryData<string> SnapshotDirs => FindSnapshotDirs();

    private static TheoryData<string> FindSnapshotDirs([CallerFilePath] string testFilePath = null!)
    {
        var testsDirPath = Path.Join(Path.GetDirectoryName(testFilePath), "Snapshots");
        var result = new TheoryData<string>();
        foreach (var snapshotDirPath in Directory.EnumerateDirectories(testsDirPath))
        {
            result.Add(snapshotDirPath);
        }
        return result;
    }
}

public class TestFileSystem : IFileSystem
{
    public TestFileSystem(string snapshotDirPath)
    {
        SnapshotDirPath = snapshotDirPath;
        SnapshotsRootPath = Path.GetDirectoryName(snapshotDirPath)!;
    }

    public string SnapshotDirPath { get; }
    public string SnapshotsRootPath { get; }

    private string TranslatePath(string path, bool canUseRoot)
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
        var original = ReadAllText(path);
        var patch = new Patch().create(Path.GetFileName(path), original, contents);
        var target = $"{TranslatePath(path, canUseRoot: false)}.patch";
        File.WriteAllText(target, patch);
    }
}
