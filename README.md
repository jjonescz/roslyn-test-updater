# Roslyn test updater

This simple command-line tool automatically updates snapshot tests in the [Roslyn repository](https://github.com/dotnet/roslyn).

> **Warning**
> Ironically, this tool does not use Roslyn to modify the sources, only a simple custom parser.
> Therefore, it often fails for tests formatted in non-standard way.
> However, PRs are welcome!

## Usage

Provide (failed) test outputs on stdin (see [an example of accepted input](https://github.com/jjonescz/roslyn-test-updater/blob/91efb6c3b23b03f09a65e22d183dd6522eabe04b/Program.cs#L191-L246)).
The tool automatically patches testing code to match reality.

> **Warning**
> It is recommended to have your Git working directory clean, so you can see the changes made by the tool, and if some are incorrect, you can simply undo them.

Furthermore, the tool writes `test.playlist` into the current directory with a list of failed tests (in Visual Studio Test Explorer format).

### From command line

`dotnet test | RoslynTestUpdater` should work.

### From Visual Studio

1. Clear Tests Output:

   <img width="545" alt="image" src="https://user-images.githubusercontent.com/3669664/191704526-3b49c291-8c19-49e8-909b-54609b38542a.png">

2. Run your tests:

   <img width="691" alt="image" src="https://user-images.githubusercontent.com/3669664/191704901-a75ba044-4e6f-4a97-9a58-40d35012b7af.png">
   
3. Copy Tests Output (<kbd>Ctrl+A</kbd>, <kbd>Ctrl+C</kbd>):

   <img width="410" alt="image" src="https://user-images.githubusercontent.com/3669664/191706206-08f857b6-7eba-4c0e-841d-378f942ad16e.png">
   
4. Make sure your Git working directory is clean:

   <img width="375" alt="image" src="https://user-images.githubusercontent.com/3669664/191706479-c101dc5a-9729-426a-bbae-cbe659b943f5.png">

5. Start `RoslynTestUpdater` with clipboard contents as input. In PowerShell:

   ```ps1
   # DON'T copy-paste this code snippet, you would overwrite your clipboard contents 😉
   Get-Clipboard | RoslynTestUpdater
   ```

6. Test code is automatically patched 🎉:

   <img width="953" alt="image" src="https://user-images.githubusercontent.com/3669664/191708607-2dc61d36-bd16-4701-90ea-bbc321c31478.png">
