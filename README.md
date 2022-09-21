# Roslyn test updater

This simple command-line tool automatically updates snapshot tests in the [Roslyn repository](https://github.com/dotnet/roslyn).

> **Warning**
> Ironically, this tool does not use Roslyn to modify the sources, only a simple custom parser.
> Therefore, it often fails for tests formatted in non-standard way.
> However, PRs are welcome!

## Usage

Provide (failed) test outputs on stdin (see [example](https://github.com/jjonescz/roslyn-test-updater/blob/91efb6c3b23b03f09a65e22d183dd6522eabe04b/Program.cs#L191-L246)).
The tool automatically updates test files.

> **Warning**
> It is recommended to have your Git working folder clean, so if the tool fails, you can simply undo the changes.

Furthermore, the tool writes `test.playlist` into the current directory with a list of failed tests (in Visual Studio Test Explorer format).
