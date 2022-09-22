using System.Runtime.CompilerServices;

namespace RoslynTestUpdater.Tests;

public class IntegrationTests
{
    [Theory]
    [MemberData(nameof(SnapshotDirs))]
    public void Snapshots(string snapshotDirPath)
    {
        Assert.Equal("0001", Path.GetFileName(snapshotDirPath));
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
