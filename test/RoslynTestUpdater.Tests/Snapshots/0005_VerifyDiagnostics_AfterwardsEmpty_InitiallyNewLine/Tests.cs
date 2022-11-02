[Fact]
public void AnonymousMethodWithExplicitDefaultParam()
{
    var source = """
        class Program
        {
            public void M()
            {
                var lam = delegate(int x = 7) { return x; };
                lam();
            }
        }

        """;
    var comp = CreateCompilation(source);
    comp.VerifyDiagnostics(
        // (5,34): error CS1065: Default values are not valid in this context.
        //         var lam = delegate(int x = 7) { return x; };
        Diagnostic(ErrorCode.ERR_DefaultValueNotAllowed, "=").WithLocation(5, 34)
        );
}
