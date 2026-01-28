using Microsoft.Extensions.Logging;
using System.CommandLine;
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

    static int Main(string[] args)
    {
        var writePlaylistOption = new Option<bool>("--write-playlist");
        var inputOption = new Option<string>("--input")
        {
            Description = "Path to the input file. If not specified, standard input is used.",
        };
        var command = new RootCommand(nameof(RoslynTestUpdater))
        {
            writePlaylistOption,
            inputOption,
        };
        var parsedArgs = command.Parse(args);

        // Validate args.
        var argsParsingResult = parsedArgs.Invoke();
        if (argsParsingResult != 0)
        {
            return argsParsingResult;
        }

        var program = new Program(new PhysicalFileSystem())
        {
            WriteTestPlaylist = parsedArgs.GetValue(writePlaylistOption),
        };

        if (parsedArgs.GetValue(inputOption) is string inputPath)
        {
            using var file = File.OpenText(inputPath);
            program.Run(file);
            return 0;
        }

        program.Run(Console.In);
        return 0;
    }

    private readonly ILogger logger;
    private readonly IFileSystem fileSystem;

    public Program(IFileSystem fileSystem)
    {
        this.logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("RoslynTestUpdater");
        this.fileSystem = fileSystem;
    }

    public bool WriteTestPlaylist { get; init; } = true;

    public void Run(TextReader input)
    {
        // Find blocks to rewrite.
        var cache = new Dictionary<string, string>();
        var blocks = new Dictionary<string, List<Replacement>>();
        var classNames = new HashSet<string>();
        var testMethods = new HashSet<(string, string, string)>();
        var counter = 0;
        foreach (var result in ParseTestOutput(input, clear: () =>
        {
            blocks.Clear();
            classNames.Clear();
            testMethods.Clear();
            counter = 0;
        }))
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
        if (WriteTestPlaylist)
        {
            var playlistPath = fileSystem.GetFullPath("test.playlist");
            using var file = fileSystem.CreateText(playlistPath);
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

        // Skip to the line reported in the stack trace.
        for (var i = 0; i < source.Line - 1; i++)
        {
            if (!reader.ReadLine())
            {
                logger.LogWarning($"Cannot find {source}; the file ends on line {i + 1}");
                return null;
            }
        }

        // Handle `verifier.VerifyDiagnostics();` (empty expected lines).
        if (expected.Count == 0)
        {
            return HandleEmptyExpectedBlock(reader, actual);
        }

        // Find current lines (printed as "Expected:" in the test output).
        LinePositionPair? start = null;
        LinePositionPair? positionBeforeCommentBlock = null;
        var searchingLine = 0;
        while (searchingLine < expected.Count)
        {
            if (!reader.ReadLine())
            {
                logger.LogWarning($"Unexpected EOF after expected line {searchingLine} for {source}");
                return null;
            }

            // Have we found the next expected code line?
            var needle = expected[searchingLine];
            if (reader.LastLine.TrimStart().StartsWith(needle, StringComparison.Ordinal))
            {
                if (searchingLine == 0)
                {
                    start = positionBeforeCommentBlock is LinePositionPair p ? p : reader.PositionPair;
                }
                searchingLine++;
                continue;
            }

            // Ignore comments but remember their count.
            if (reader.LastLine.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                positionBeforeCommentBlock ??= reader.PositionPair;
                continue;
            }

            // Ignore anything when searching for the first line.
            if (searchingLine == 0)
            {
                continue;
            }

            // Otherwise, ignore also empty lines.
            if (reader.LastLine.Length == 0)
            {
                continue;
            }

            // But nothing else.
            logger.LogWarning($"Found unexpected line {reader.LineCount} in expected block for {source}: {reader.LastLine}");
            return null;
        }
        if (start is null)
        {
            logger.LogWarning($"Cannot find start of expected block for {source}");
            return null;
        }

        var indent = reader.DetectIndentation();

        // Append `);` (repeat the parenthesis as it was on the input).
        string suffix;
        if (closingRegex.Match(reader.LastLine.ToString()) is { } m && m.Success)
        {
            if (reader.LastLine[..m.Index].IsWhiteSpace())
            {
                // The closing parenthesis is on a separate line.
                suffix = $"{reader.LastLineEnd}{indent}{m.ValueSpan}";
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

        // Handle empty actual block.
        if (string.IsNullOrWhiteSpace(actual))
        {
            var s = start.Value.PreviousOrLast.BeforeEndOfLine;

            // Handle closing parenthesis on a separate line.
            if (reader.PeekLine(out var next) && closingRegex.Match(next.LastLine.ToString()) is { } n && n.Success)
            {
                return new(s, next.Position.BeforeEndOfLine, n.Value);
            }

            return new(s, reader.Position.BeforeEndOfLine, suffix);
        }

        return new(start.Value.Last.StartOfLine, reader.Position.BeforeEndOfLine, IndentAndNormalize(reader, indent, actual) + suffix);
    }

    static Replacement HandleEmptyExpectedBlock(LineReader reader, string actual)
    {
        reader.ReadLine();
        int start;
        if (closingRegex.Match(reader.LastLine.ToString()) is { } m && m.Success)
        {
            // Start just before the closing `);` if possible.
            start = reader.Position.StartOfLine + m.Index;
        }
        else
        {
            // Otherwise, start at the end of the line.
            start = reader.Position.BeforeEndOfLine;
        }

        // Indent one more than actual.
        var indent = reader.DetectIndentation() + "    ";
        return new(start, reader.Position.BeforeEndOfLine,
           reader.LastLineEnd.ToString() +
           IndentAndNormalize(reader, indent, actual) +
           ");");
    }

    static string IndentAndNormalize(in LineReader reader, string indent, string actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return string.Empty;
        }
        return Indent(indent, actual).ReplaceLineEndings(reader.LastLineEnd.ToString());
    }

    IEnumerable<ParsingResult> ParseTestOutput(TextReader reader, Action clear)
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
                    const string startingTestRun = "========== Starting test run ==========";
                    if (line.Contains(startingTestRun))
                    {
                        logger.LogInformation($"Found '{startingTestRun}', forgetting results so far.");
                        clear();
                        continue;
                    }

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

public readonly record struct LinePositionPair(LinePosition? Previous, LinePosition Last)
{
    public LinePosition PreviousOrLast => Previous ?? Last;
}

public readonly record struct LinePosition
{
    /// <summary>
    /// Starts at 1. Currently only used for debugging.
    /// </summary>
    public required int LineNumber { get; init; }

    public required int StartOfLine { get; init; }
    public required int BeforeEndOfLine { get; init; }
    public required int AfterEndOfLine { get; init; }

    public static LinePosition Create(int lineNumber, int index, int length, in ReadOnlySpan<char> lineEnd)
    {
        var afterEndOfLine = index + length;
        return new()
        {
            LineNumber = lineNumber,
            StartOfLine = index,
            BeforeEndOfLine = afterEndOfLine - lineEnd.Length,
            AfterEndOfLine = afterEndOfLine,
        };
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
    public LinePosition? PreviousPosition { get; private set; }
    public LinePosition Position { get; private set; }
    public LinePositionPair PositionPair => new(PreviousPosition, Position);
    public int LineCount { get; private set; }
    public ReadOnlySpan<char> LastLine => PreviousPosition is { } prev ? Input.AsSpan()[prev.AfterEndOfLine..Position.BeforeEndOfLine] : default;
    public ReadOnlySpan<char> LastLineEnd => Input.AsSpan()[Position.BeforeEndOfLine..Position.AfterEndOfLine];

    public bool PeekLine(out LineReader result)
    {
        if (endOfLineRegex.Match(Input, Position.AfterEndOfLine) is { } m && m.Success)
        {
            result = new LineReader(Input)
            {
                PreviousPosition = Position,
                Position = new()
                {
                    LineNumber = LineCount + 1,
                    StartOfLine = Position.AfterEndOfLine,
                    BeforeEndOfLine = m.Index,
                    AfterEndOfLine = m.Index + m.Length,
                },
                LineCount = LineCount + 1,
            };
            return true;
        }
        result = this;
        return false;
    }

    public bool ReadLine()
    {
        if (PeekLine(out var result))
        {
            PreviousPosition = result.PreviousPosition;
            Position = result.Position;
            LineCount = result.LineCount;
            return true;
        }
        return false;
    }

    public string DetectIndentation()
    {
        return string.Join(null, LastLine.ToString().TakeWhile(char.IsWhiteSpace));
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
