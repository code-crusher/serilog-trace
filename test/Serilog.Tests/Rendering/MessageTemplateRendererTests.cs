namespace Serilog.Tests.Rendering;

public class MessageTemplateRendererTests
{
    readonly MessageTemplateParser _messageTemplateParser = new();

    [Theory]
    [InlineData("{Number}", null, "16")]
    [InlineData("{Number:X8}", null, "00000010")]
    [InlineData("{Number}", "j", "16")]
    [InlineData("{Number:X8}", "j", "00000010")]
    public void PropertyTokenFormatsAreApplied(string template, string? appliedFormat, string expected)
    {
        var eventTemplate = _messageTemplateParser.Parse(template);
        var properties = new Dictionary<string, LogEventPropertyValue> { ["Number"] = new ScalarValue(16) };

        var output = new StringWriter();
        MessageTemplateRenderer.Render(eventTemplate, properties, output, appliedFormat, CultureInfo.InvariantCulture);

        Assert.Equal(expected, output.ToString());
    }

    [Theory]
    [InlineData(null, "Hello, world!")]
    [InlineData("l", "Hello, world!")]
    [InlineData("lj", "Hello, world!")]
    [InlineData("jl", "Hello, world!")]
    [InlineData("j", "Hello, \"world\"!")]
    public void StringsAreRenderedAsLiteralsByDefault(string? appliedFormat, string expected)
    {
        var eventTemplate = _messageTemplateParser.Parse("Hello, {Name}!");
        var properties = new Dictionary<string, LogEventPropertyValue> { ["Name"] = new ScalarValue("world") };

        var output = new StringWriter();
        MessageTemplateRenderer.Render(eventTemplate, properties, output, appliedFormat, CultureInfo.InvariantCulture);

        Assert.Equal(expected, output.ToString());
    }

    [Theory]
    [InlineData(null, "{\"Name\":\"world\"}")]
    [InlineData("j", "{\"Name\":\"world\"}")]
    [InlineData("lj", "{\"Name\":\"world\"}")]
    [InlineData("jl", "{\"Name\":\"world\"}")]
    [InlineData("l", "{ Name: \"world\" }")]
    public void StructuresAreRenderedAsJsonByDefault(string? appliedFormat, string expected)
    {
        var eventTemplate = _messageTemplateParser.Parse("{@Obj}");
        var properties = new Dictionary<string, LogEventPropertyValue>
        {
            ["Obj"] = new StructureValue([new LogEventProperty("Name", new ScalarValue("world"))])
        };

        var output = new StringWriter();
        MessageTemplateRenderer.Render(eventTemplate, properties, output, appliedFormat, CultureInfo.InvariantCulture);

        Assert.Equal(expected, output.ToString());
    }

    [Theory]
    [InlineData(null, "[1,\"two\"]")]
    [InlineData("j", "[1,\"two\"]")]
    [InlineData("l", "[1, \"two\"]")]
    public void SequencesAreRenderedAsJsonByDefault(string? appliedFormat, string expected)
    {
        var eventTemplate = _messageTemplateParser.Parse("{List}");
        var properties = new Dictionary<string, LogEventPropertyValue>
        {
            ["List"] = new SequenceValue([new ScalarValue(1), new ScalarValue("two")])
        };

        var output = new StringWriter();
        MessageTemplateRenderer.Render(eventTemplate, properties, output, appliedFormat, CultureInfo.InvariantCulture);

        Assert.Equal(expected, output.ToString());
    }
}
