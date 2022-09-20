using System.Text;
using System.Text.RegularExpressions;

namespace RoslynTestUpdater;

internal class Program
{
    enum State
    {
        Searching,
        FoundFailedTest,
        FoundActual,
        AfterActual,
        FoundStackTrace,
    }

    readonly record struct FileAndLocation(string Path, int Line, int Column);
    
    readonly record struct Replacement(int Start, int End, string Target);

    static readonly Regex stackTraceEntryRegex = new(@"\((\d+),(\d+)\): at ", RegexOptions.Compiled);

    static readonly Regex endOfLineRegex = new(@"\r\n|[\r\n]|$", RegexOptions.Compiled);

    static void Main()
    {
        // Find blocks to rewrite.
        var cache = new Dictionary<string, string>();
        var blocks = new Dictionary<string, List<Replacement>>();
        foreach (var (actual, source) in ParseTestOutput())
        {
            var (start, end) = FindExpectedBlock(cache, source);
            if (!blocks.TryGetValue(source.Path, out var list))
            {
                list = new(1);
                blocks.Add(source.Path, list);
            }
            list.Add(new(start, end, actual));
        }

        // Construct new file contents.
        foreach (var (file, replacements) in blocks)
        {
            var contents = cache[file];

            var delta = 0;
            foreach (var (start, end, replacement) in replacements)
            {
                contents = contents[..(start + delta)] + replacement + contents[(end + delta + 1)..];
                delta += replacement.Length - end + start;
            }

            File.WriteAllText(file, contents);
        }
    }

    static (int Start, int End) FindExpectedBlock(IDictionary<string, string> cache, FileAndLocation source)
    {
        // Get and cache file contents.
        if (!cache.TryGetValue(source.Path, out var contents))
        {
            contents = File.ReadAllText(source.Path);
            cache.Add(source.Path, contents);
        }

        // Find the line.
        var position = 0;
        for (var i = 0; i < source.Line; i++)
        {
            var match = endOfLineRegex.Match(contents, position);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Cannot find {source}; the file ends on line {i + 1}");
            }
            position = match.Index + match.Length;
        }

        // Get indented block starting on the next line.
        var start = position;
        var firstLineMatch = endOfLineRegex.Match(contents, position);
        if (!firstLineMatch.Success)
        {
            throw new InvalidOperationException($"Unexpected EOF just after {source}");
        }
        position = firstLineMatch.Index + firstLineMatch.Length;
        var firstLine = contents[start..firstLineMatch.Index];
        var indent = string.Join(null, firstLine.TakeWhile(char.IsWhiteSpace));
        var end = start;
        while (endOfLineRegex.Match(contents, position) is { } m && m.Success)
        {
            var line = contents[position..m.Index];
            position = m.Index + m.Length;
            if (line.StartsWith(indent))
            {
                end = position;
            }
            else
            {
                return (start, end);
            }
        }
        throw new InvalidOperationException($"Unexpected EOF while finding block at {source}");
    }

    static IEnumerable<(string Actual, FileAndLocation Source)> ParseTestOutput()
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
        var actual = new StringBuilder();
        FileAndLocation? lastStackTraceLine = null;
        var readNextLine = true;
        string? GetNextLine(string? line)
        {
            if (readNextLine)
            {
                return Console.ReadLine();
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

                // Find block with actual diagnostics.
                case State.FoundFailedTest:
                    if (!line.EndsWith("Actual:"))
                    {
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
                        // Extract file name from the stack trace.
                        lastStackTraceLine = new(
                            Path: RemoveIndent(line[..match.Index]),
                            Line: int.Parse(match.Groups[1].ValueSpan),
                            Column: int.Parse(match.Groups[2].ValueSpan));
                        continue;
                    }
                    if (lastStackTraceLine != null)
                    {
                        yield return (actual.ToString(), lastStackTraceLine.Value);
                    }
                    readNextLine = false;
                    state = State.Searching;
                    continue;

                default:
                    throw new InvalidOperationException($"Unexpected state: {state}");
            }
        }
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
}
