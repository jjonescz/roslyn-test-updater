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
            // (5,31): error CS9501: Parameter 1 has default value '2' in lambda and '<missing>' in the target delegate type.
            //         var lam = delegate(int x = 7) { return x; };
            Diagnostic(ErrorCode.ERR_OptionalParamValueMismatch, "i").WithArguments("1", "7", "<missing>").WithLocation(5, 31));
}
