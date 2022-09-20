﻿using System.Text;
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

    static readonly Regex startRegex = new("^(?!$)", RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex endOfLineRegex = new(@"\r\n|[\r\n]", RegexOptions.Compiled);

    // UTF8 with BOM
    static readonly Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    static void Main()
    {
        // Find blocks to rewrite.
        var cache = new Dictionary<string, string>();
        var blocks = new Dictionary<string, List<Replacement>>();
        var counter = 0;
        foreach (var (actual, source) in ParseTestOutput())
        {
            Console.WriteLine($"Found test output: {++counter}");
            var replacement = FindExpectedBlock(cache, actual, source);
            if (!blocks.TryGetValue(source.Path, out var list))
            {
                list = new(1);
                blocks.Add(source.Path, list);
            }
            list.Add(replacement);
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

            File.WriteAllText(file, contents, encoding);
            Console.WriteLine("Done.");
        }
    }

    static Replacement FindExpectedBlock(IDictionary<string, string> cache, string actual, FileAndLocation source)
    {
        // Get and cache file contents.
        if (!cache.TryGetValue(source.Path, out var contents))
        {
            Console.Write($"Reading {source.Path}... ");
            contents = File.ReadAllText(source.Path);
            cache.Add(source.Path, contents);
            Console.WriteLine("Done.");
        }

        // Find the line.
        var position = 0;
        for (var i = 0; i < source.Line - 1; i++)
        {
            var match = endOfLineRegex.Match(contents, position);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Cannot find {source}; the file ends on line {i + 1}");
            }
            position = match.Index + match.Length;
        }

        // Skip to line that actually contains the diagnostics call.
        var backup = position;
        while (true)
        {
            if (endOfLineRegex.Match(contents, position) is { } m && m.Success)
            {
                var line = contents[position..m.Index];
                position = m.Index + m.Length;
                if (line.Contains("Diagnostics("))
                {
                    break;
                }
            }
            else
            {
                throw new InvalidOperationException($"Unexpected EOF while finding diagnostics call at {source}");
            }
        }

        // Get indented block starting on the next line.
        var firstLineMatch = endOfLineRegex.Match(contents, position);
        if (!firstLineMatch.Success)
        {
            throw new InvalidOperationException($"Unexpected EOF just after {source}");
        }
        var firstLine = contents[position..firstLineMatch.Index];
        var lineEnd = firstLineMatch.Value;
        var indent = string.Join(null, firstLine.TakeWhile(char.IsWhiteSpace));
        if (indent.Length == 0)
        {
            throw new InvalidOperationException($"Cannot find indent at {source}");
        }
        var start = position;
        var end = start;
        var lastLine = firstLine;
        position = firstLineMatch.Index + firstLineMatch.Length;
        while (endOfLineRegex.Match(contents, position) is { } m && m.Success)
        {
            var line = contents[position..m.Index];
            if (line.StartsWith(indent))
            {
                end = m.Index;
                lastLine = contents[position..end];
                position = end + m.Length;
            }
            else
            {
                // Append `);`.
                const string closing = ");";
                string suffix;
                if (lastLine.Trim() == closing)
                {
                    suffix = lineEnd + indent + closing;
                }
                else if (lastLine.EndsWith(closing))
                {
                    suffix = closing;
                }
                else
                {
                    suffix = string.Empty;
                }
                return new(start, end, Indent(indent, actual).ReplaceLineEndings(lineEnd) + suffix);
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
                        yield return (actual.ToString().TrimEnd(), lastStackTraceLine.Value);
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

    static string Indent(string indent, string block)
    {
        return startRegex.Replace(block, indent);
    }
}
