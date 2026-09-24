using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

public class TypeTokenTests {
    // The XAML language schema registers exactly these eleven primitives under x:.
    // Anything else under x: fails at load with "Cannot create unknown type".
    [Theory]
    [InlineData("String", "x:String")]
    [InlineData("Int32", "x:Int32")]
    [InlineData("Int64", "x:Int64")]
    [InlineData("Double", "x:Double")]
    [InlineData("Boolean", "x:Boolean")]
    [InlineData("Byte", "x:Byte")]
    [InlineData("Single", "x:Single")]
    [InlineData("Decimal", "x:Decimal")]
    [InlineData("Char", "x:Char")]
    [InlineData("Object", "x:Object")]
    [InlineData("TimeSpan", "x:TimeSpan")]
    public void Render_XamlPrimitives_GetXPrefix(string input, string expected) =>
        Assert.Equal(expected, TypeToken.Render(input));

    [Theory]
    [InlineData("DateTime", "s:DateTime")]
    [InlineData("DateTimeOffset", "s:DateTimeOffset")]
    [InlineData("Guid", "s:Guid")]
    [InlineData("Uri", "s:Uri")]
    [InlineData("Exception", "s:Exception")]
    [InlineData("Type", "s:Type")]
    public void Render_SystemTypesNotInXamlSchema_GetSPrefix(string input, string expected) =>
        Assert.Equal(expected, TypeToken.Render(input));

    [Theory]
    [InlineData("DataTable", "sd:DataTable")]
    [InlineData("DataRow", "sd:DataRow")]
    [InlineData("DataColumn", "sd:DataColumn")]
    public void Render_SystemDataTypes_GetSdPrefix(string input, string expected) =>
        Assert.Equal(expected, TypeToken.Render(input));

    [Theory]
    [InlineData("Invoice", "Invoice")]                        // unknown bare name passes through
    [InlineData("System.Data.DataTable", "System.Data.DataTable")] // dotted passes through
    [InlineData("x:String", "x:String")]                      // already qualified
    [InlineData("scg:List(x:String)", "scg:List(x:String)")]
    [InlineData("  String  ", "x:String")]                     // trimmed
    public void Render_AlreadyQualifiedOrUnknown_PassesThrough(string input, string expected) =>
        Assert.Equal(expected, TypeToken.Render(input));

    [Theory]
    [InlineData("x:String", null)]
    [InlineData("s:DateTime", "s")]
    [InlineData("sd:DataRow", "sd")]
    [InlineData("scg:IEnumerable(x:String)", "scg")]
    [InlineData("DataRow", null)]
    [InlineData("", null)]
    public void AliasOf_ReportsOnlyAliasesNeedingADeclaration(string token, string? expected) =>
        Assert.Equal(expected, TypeToken.AliasOf(token));

    [Fact]
    public void AliasesIn_SplitsCommaSeparatedTypeArguments() {
        var aliases = TypeToken.AliasesIn("x:String, sd:DataRow, Argument").ToList();

        Assert.Equal(["sd"], aliases);
    }

    [Fact]
    public void AliasesIn_NullOrBlank_IsEmpty() {
        Assert.Empty(TypeToken.AliasesIn(null));
        Assert.Empty(TypeToken.AliasesIn("   "));
    }
}
