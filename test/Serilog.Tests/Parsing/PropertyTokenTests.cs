using PropertyToken = Serilog.Parsing.PropertyToken;

namespace Serilog.Tests.Parsing;

public class PropertyTokenTests
{
    static PropertyToken Parse(string messageTemplate)
    {
        return (PropertyToken)new MessageTemplateParser().Parse(messageTemplate).Tokens.Single();
    }

    [Fact]
    public void StringsAreRenderedAsLiteralsByDefault()
    {
        var pt = Parse("{Name}");
        var properties = new Dictionary<string, LogEventPropertyValue> { ["Name"] = new ScalarValue("world") };

        var output = new StringWriter();
        pt.Render(properties, output);

        Assert.Equal("world", output.ToString());
    }

    [Fact]
    public void StructuresAreRenderedAsJsonByDefault()
    {
        var pt = Parse("{@Obj}");
        var properties = new Dictionary<string, LogEventPropertyValue>
        {
            ["Obj"] = new StructureValue([new LogEventProperty("Name", new ScalarValue("world"))])
        };

        var output = new StringWriter();
        pt.Render(properties, output);

        Assert.Equal("{\"Name\":\"world\"}", output.ToString());
    }
}
