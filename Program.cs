using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynTestUpdater;

public class Program
{
    enum State
    {
        Searching,
        FoundFailedTest,
        FoundExpected,
        FoundActual,
        AfterActual,
        FoundStackTrace,
    }

    readonly record struct FileAndLocation(string Path, int Line, int Column, string Namespace, string ClassName, string MethodName);

    readonly record struct ParsingResult(IReadOnlyList<string> Expected, string Actual, FileAndLocation Source);

    readonly record struct Replacement(int Start, int End, string Target);

    static readonly Regex stackTraceEntryRegex = new(@"\((\d+),(\d+)\): at ((\w+\.)*)(\w+)\.(\w+)", RegexOptions.Compiled);

    static readonly Regex startRegex = new("^(?!$)", RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex closingRegex = new(@"\)*;$", RegexOptions.Compiled);

    // UTF8 with BOM
    static readonly Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    static readonly string[] clues = new[]
    {
        "Diagnostics(",
        "Verify(",
    };

    static void Main()
    {
        var program = new Program(new PhysicalFileSystem());
        program.Run(Console.In);
    }

    private readonly ILogger logger;
    private readonly IFileSystem fileSystem;

    public Program(IFileSystem fileSystem)
    {
        this.logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("RoslynTestUpdater");
        this.fileSystem = fileSystem;
    }

    public void Run(TextReader input)
    {
        // Find blocks to rewrite.
        var cache = new Dictionary<string, string>();
        var blocks = new Dictionary<string, List<Replacement>>();
        var classNames = new HashSet<string>();
        var testMethods = new HashSet<(string, string, string)>();
        var counter = 0;
        foreach (var result in ParseTestOutput(input))
        {
            if (!testMethods.Add((result.Source.Namespace, result.Source.ClassName, result.Source.MethodName)))
            {
                Console.WriteLine($"Skipped duplicate test: {++counter} ({result.Source})");
                continue;
            }
            Console.WriteLine($"Found test output: {++counter}");
            if (FindExpectedBlock(cache, result) is { } replacement)
            {
                if (!blocks.TryGetValue(result.Source.Path, out var list))
                {
                    list = new(1);
                    blocks.Add(result.Source.Path, list);
                }
                list.Add(replacement);
                classNames.Add(result.Source.ClassName);
            }
        }

        // Construct new file contents.
        foreach (var (file, replacements) in blocks)
        {
            Console.Write($"Replacing {file}... ");
            var contents = cache[file];

            replacements.Sort((x, y) => x.Start.CompareTo(y.Start));

            var delta = 0;
            foreach (var (start, end, replacement) in replacements)
            {
                contents = contents[..(start + delta)] + replacement + contents[(end + delta + 1)..];
                delta += replacement.Length - (end + 1 - start);
            }

            fileSystem.WriteAllText(file, contents, encoding);
            Console.WriteLine("Done.");
        }

        // Write test playlist.
        var playlistPath = fileSystem.GetFullPath("test.playlist");
        using (var file = fileSystem.CreateText(playlistPath))
        {
            file.WriteLine("<Playlist Version=\"2.0\"><Rule Match=\"Any\">");
            foreach (var className in classNames)
            {
                file.WriteLine($"<Property Name=\"Class\" Value=\"{className}\" />");
            }
            file.WriteLine("</Rule></Playlist>");
            Console.WriteLine($"Wrote {playlistPath}");
        }
    }

    Replacement? FindExpectedBlock(IDictionary<string, string> cache, ParsingResult parsingResult)
    {
        var (expected, actual, source) = parsingResult;

        // Get and cache file contents.
        if (!cache.TryGetValue(source.Path, out var contents))
        {
            Console.Write($"Reading {source.Path}... ");
            contents = fileSystem.ReadAllText(source.Path);
            cache.Add(source.Path, contents);
            Console.WriteLine("Done.");
        }

        var reader = new LineReader(contents);

        // Find the line.
        for (var i = 0; i < source.Line - 1; i++)
        {
            if (!reader.ReadLine())
            {
                logger.LogWarning($"Cannot find {source}; the file ends on line {i + 1}");
                return null;
            }
        }

        // Skip to line that actually contains the diagnostics call.
        while (true)
        {
            if (reader.ReadLine())
            {
                foreach (var clue in clues)
                {
                    if (reader.LastLine.Contains(clue, StringComparison.Ordinal))
                    {
                        goto afterLoop;
                    }
                }
            }
            else
            {
                logger.LogWarning($"Unexpected EOF while finding diagnostics call at {source}");
                return null;
            }
        }
    afterLoop:

        // Get indented block starting on the next line.
        var start = reader.Position;
        if (!reader.ReadLine())
        {
            logger.LogWarning($"Unexpected EOF just after {source}");
            return null;
        }
        var lineEnd = reader.LastLineEnd.ToString();
        var indent = string.Join(null, reader.LastLine.ToString().TakeWhile(char.IsWhiteSpace));
        if (indent.Length == 0)
        {
            logger.LogWarning($"Cannot find indent at {source}");
            return null;
        }
        var end = reader.PositionBeforeLineEnd;
        var prevLine = reader.LastLine;
        while (reader.ReadLine())
        {
            // Read as long as the indentation is the same (but ignore empty lines).
            if (reader.LastLine.Length == 0 || (reader.LastLine.Length > indent.Length &&
                reader.LastLine.StartsWith(indent, StringComparison.Ordinal) &&
                !reader.LastLine[indent.Length..(indent.Length + 1)].IsWhiteSpace()))
            {
                prevLine = reader.LastLine;
                end = reader.PositionBeforeLineEnd;
            }
            else
            {
                // Append `);` (repeat the parenthesis as it was on the input).
                string suffix;
                if (closingRegex.Match(prevLine.ToString()) is { } m && m.Success)
                {
                    if (prevLine[..m.Index].IsWhiteSpace())
                    {
                        // The parenthesis is on a separate line.
                        suffix = $"{lineEnd}{indent}{m.ValueSpan}";
                    }
                    else
                    {
                        suffix = m.Value[1..];
                    }
                }
                else
                {
                    suffix = string.Empty;
                }
                return new(start, end, Indent(indent, actual).ReplaceLineEndings(lineEnd) + suffix);
            }
        }
        logger.LogWarning($"Unexpected EOF while finding block at {source}");
        return null;
    }

    static IEnumerable<ParsingResult> ParseTestOutput(TextReader reader)
    {
        /*
[xUnit.net 00:00:07.38]     Microsoft.CodeAnalysis.CSharp.UnitTests.RefFieldTests.AssignValueTo_InstanceMethod_RefReadonlyField [FAIL]
[xUnit.net 00:00:07.38]       
[xUnit.net 00:00:07.38]       Expected:
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(7, 9),
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(8, 9),
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(9, 9),
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(10, 9),
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(16, 13),
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(17, 13),
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(18, 13)
[xUnit.net 00:00:07.38]       Actual:
[xUnit.net 00:00:07.38]                       // (7,9): error CS8329: Cannot use field 'S<T>.F' as a ref or out value because it is a readonly variable
[xUnit.net 00:00:07.38]                       //         F = tValue; // 1
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(7, 9),
[xUnit.net 00:00:07.38]                       // (8,9): error CS8329: Cannot use field 'S<T>.F' as a ref or out value because it is a readonly variable
[xUnit.net 00:00:07.38]                       //         F = tRef; // 2
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(8, 9),
[xUnit.net 00:00:07.38]                       // (9,9): error CS8329: Cannot use field 'S<T>.F' as a ref or out value because it is a readonly variable
[xUnit.net 00:00:07.38]                       //         F = tOut; // 3
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(9, 9),
[xUnit.net 00:00:07.38]                       // (10,9): error CS8329: Cannot use field 'S<T>.F' as a ref or out value because it is a readonly variable
[xUnit.net 00:00:07.38]                       //         F = tIn; // 4
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(10, 9),
[xUnit.net 00:00:07.38]                       // (16,13): error CS8329: Cannot use field 'S<T>.F' as a ref or out value because it is a readonly variable
[xUnit.net 00:00:07.38]                       //             F = GetValue(); // 5
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(16, 13),
[xUnit.net 00:00:07.38]                       // (17,13): error CS8329: Cannot use field 'S<T>.F' as a ref or out value because it is a readonly variable
[xUnit.net 00:00:07.38]                       //             F = GetRef(); // 6
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(17, 13),
[xUnit.net 00:00:07.38]                       // (18,13): error CS8329: Cannot use field 'S<T>.F' as a ref or out value because it is a readonly variable
[xUnit.net 00:00:07.38]                       //             F = GetRefReadonly(); // 7
[xUnit.net 00:00:07.38]                       Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(18, 13)
[xUnit.net 00:00:07.38]       Diff:
[xUnit.net 00:00:07.38]       ++>                 Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(7, 9)
[xUnit.net 00:00:07.38]       ++>                 Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(8, 9)
[xUnit.net 00:00:07.38]       ++>                 Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(9, 9)
[xUnit.net 00:00:07.38]       ++>                 Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(10, 9)
[xUnit.net 00:00:07.38]       ++>                 Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(16, 13)
[xUnit.net 00:00:07.38]       ++>                 Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(17, 13)
[xUnit.net 00:00:07.38]       ++>                 Diagnostic(ErrorCode.ERR_RefReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(18, 13)
[xUnit.net 00:00:07.38]       -->                 Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(7, 9)
[xUnit.net 00:00:07.38]       -->                 Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(8, 9)
[xUnit.net 00:00:07.38]       -->                 Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(9, 9)
[xUnit.net 00:00:07.38]       -->                 Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(10, 9)
[xUnit.net 00:00:07.38]       -->                 Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(16, 13)
[xUnit.net 00:00:07.38]       -->                 Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(17, 13)
[xUnit.net 00:00:07.38]       -->                 Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "F").WithArguments("field", "S<T>.F").WithLocation(18, 13)
[xUnit.net 00:00:07.38]       Expected: True
[xUnit.net 00:00:07.38]       Actual:   False
[xUnit.net 00:00:07.38]       Stack Trace:
[xUnit.net 00:00:07.38]         C:\Users\janjones\Code\roslyn\src\Compilers\Test\Core\Diagnostics\DiagnosticExtensions.cs(98,0): at Microsoft.CodeAnalysis.DiagnosticExtensions.Verify(IEnumerable`1 actual, DiagnosticDescription[] expected, Boolean errorCodeOnly)
[xUnit.net 00:00:07.38]         C:\Users\janjones\Code\roslyn\src\Compilers\Test\Core\Diagnostics\DiagnosticExtensions.cs(48,0): at Microsoft.CodeAnalysis.DiagnosticExtensions.Verify(IEnumerable`1 actual, DiagnosticDescription[] expected)
[xUnit.net 00:00:07.38]         C:\Users\janjones\Code\roslyn\src\Compilers\Test\Core\Diagnostics\DiagnosticExtensions.cs(63,0): at Microsoft.CodeAnalysis.DiagnosticExtensions.Verify(ImmutableArray`1 actual, DiagnosticDescription[] expected)
[xUnit.net 00:00:07.38]         C:\Users\janjones\Code\roslyn\src\Compilers\Test\Core\Diagnostics\DiagnosticExtensions.cs(353,0): at Microsoft.CodeAnalysis.DiagnosticExtensions.VerifyEmitDiagnostics[TCompilation](TCompilation c, EmitOptions options, DiagnosticDescription[] expected)
[xUnit.net 00:00:07.38]         C:\Users\janjones\Code\roslyn\src\Compilers\Test\Core\Diagnostics\DiagnosticExtensions.cs(370,0): at Microsoft.CodeAnalysis.DiagnosticExtensions.VerifyEmitDiagnostics[TCompilation](TCompilation c, DiagnosticDescription[] expected)
[xUnit.net 00:00:07.38]         C:\Users\janjones\Code\roslyn\src\Compilers\CSharp\Test\Semantic\Semantics\RefFieldTests.cs(3946,0): at Microsoft.CodeAnalysis.CSharp.UnitTests.RefFieldTests.AssignValueTo_InstanceMethod_RefReadonlyField()
         */
        var state = State.Searching;
        var expected = new List<string>();
        var actual = new StringBuilder();
        FileAndLocation? lastStackTraceLine = null;
        var readNextLine = true;
        string? GetNextLine(string? line)
        {
            if (readNextLine)
            {
                return reader.ReadLine();
            }
            readNextLine = true;
            return line;
        }
        for (string? line = null; (line = GetNextLine(line)) != null;)
        {
            switch (state)
            {
                // Find first test which fails.
                case State.Searching:
                    if (!line.EndsWith(" [FAIL]"))
                    {
                        continue;
                    }
                    state = State.FoundFailedTest;
                    continue;

                // Find block with expected diagnostics (those currently in code).
                case State.FoundFailedTest:
                    if (!line.EndsWith("Expected:"))
                    {
                        continue;
                    }
                    state = State.FoundExpected;
                    expected.Clear();
                    continue;

                // Find block with actual diagnostics (those that the current should be replaced with).
                case State.FoundExpected:
                    if (!line.EndsWith("Actual:"))
                    {
                        expected.Add(RemoveIndent(line));
                        continue;
                    }
                    state = State.FoundActual;
                    actual.Clear();
                    continue;

                // Gather actual diagnostics.
                case State.FoundActual:
                    if (line.EndsWith("Diff:"))
                    {
                        state = State.AfterActual;
                        continue;
                    }
                    actual.AppendLine(RemoveIndent(line));
                    continue;

                // Find stack trace block.
                case State.AfterActual:
                    if (!line.EndsWith("Stack Trace:"))
                    {
                        continue;
                    }
                    state = State.FoundStackTrace;
                    continue;

                // Parse stack trace. When not possible, stack trace ended.
                case State.FoundStackTrace:
                    var match = stackTraceEntryRegex.Match(line);
                    if (match.Success)
                    {
                        // Parse the stack trace line.
                        lastStackTraceLine = new(
                            Path: RemoveIndent(line[..match.Index]),
                            Line: int.Parse(match.Groups[1].ValueSpan),
                            Column: int.Parse(match.Groups[2].ValueSpan),
                            Namespace: match.Groups[3].Value,
                            ClassName: match.Groups[5].Value,
                            MethodName: match.Groups[6].Value);
                        continue;
                    }
                    if (lastStackTraceLine != null)
                    {
                        yield return GetResult();
                    }
                    readNextLine = false;
                    state = State.Searching;
                    continue;

                default:
                    throw new InvalidOperationException($"Unexpected state: {state}");
            }
        }
        if (lastStackTraceLine != null)
        {
            yield return GetResult();
        }

        ParsingResult GetResult() => new(expected, actual.ToString().TrimEnd(), lastStackTraceLine.Value);
    }

    static string RemoveIndent(string line)
    {
        // Strip xUnit prefix.
        if (line.StartsWith("[xUnit.net"))
        {
            line = line[(line.IndexOf(']') + 1)..];
        }

        return line.TrimStart();
    }

    static string Indent(string indent, string block)
    {
        return startRegex.Replace(block, indent);
    }
}

public ref struct LineReader
{
    private static readonly Regex endOfLineRegex = new(@"\r\n|[\r\n]", RegexOptions.Compiled);

    public LineReader(string input)
    {
        Input = input;
    }

    public string Input { get; }
    public int Position { get; private set; } = 0;
    public int PositionBeforeLineEnd => Position - LastLineEnd.Length;
    public ReadOnlySpan<char> LastLine { get; private set; } = default;
    public ReadOnlySpan<char> LastLineEnd { get; private set; } = default;

    public bool ReadLine()
    {
        if (endOfLineRegex.Match(Input, Position) is { } m && m.Success)
        {
            LastLine = Input.AsSpan()[Position..m.Index];
            LastLineEnd = m.ValueSpan;
            Position = m.Index + m.Length;
            return true;
        }
        return false;
    }
}

public interface IFileSystem
{
    StreamWriter CreateText(string path);
    string GetFullPath(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string? contents, Encoding encoding);
}

public class PhysicalFileSystem : IFileSystem
{
    public StreamWriter CreateText(string path)
    {
        return File.CreateText(path);
    }

    public string GetFullPath(string path)
    {
        return Path.GetFullPath(path);
    }

    public string ReadAllText(string path)
    {
        return File.ReadAllText(path);
    }

    public void WriteAllText(string path, string? contents, Encoding encoding)
    {
        File.WriteAllText(path, contents, encoding);
    }
}
