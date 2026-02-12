using Xunit;

public sealed class OptionsParserTests
{
    [Fact]
    public void AnonymousBeforeCommand_DoesNotConsumeSubcommand()
    {
        var parsed = OptionsParser.Parse(new[] { "--anonymous", "mod", "123", "C:\\mods" });

        Assert.False(parsed.HasErrors);
        Assert.Equal(CommandKind.Mod, parsed.Options.Command);
        Assert.True(parsed.Options.UseAnonymous);
        Assert.Equal(123UL, parsed.Options.PublishedFileId);
        Assert.Equal("C:\\mods", parsed.Options.OutputDir);
    }

    [Fact]
    public void UnknownCommand_ReturnsHelpfulError()
    {
        var parsed = OptionsParser.Parse(new[] { "synce", "268500", "C:\\mods" });

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Errors, e => e.Contains("Unknown command", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WrongArgumentCount_IsReported()
    {
        var parsed = OptionsParser.Parse(new[] { "mod", "123" });

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Errors, e => e.Contains("requires exactly 2 arguments", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidPublishedFileId_IsReported()
    {
        var parsed = OptionsParser.Parse(new[] { "mod", "notANumber", "C:\\mods" });

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Errors, e => e.Contains("Invalid published file id", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OptionMissingValue_IsReported()
    {
        var parsed = OptionsParser.Parse(new[] { "mod", "123", "C:\\mods", "--user" });

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Errors, e => e.Contains("requires a value", System.StringComparison.OrdinalIgnoreCase));
    }
}

