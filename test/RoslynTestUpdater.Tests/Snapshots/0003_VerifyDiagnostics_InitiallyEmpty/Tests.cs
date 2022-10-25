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
    comp.VerifyDiagnostics();
}
