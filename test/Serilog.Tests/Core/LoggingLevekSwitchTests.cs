namespace Serilog.Tests.Core;

public class LoggingLevekSwitchTests
{
    [Fact]
    public void MinimumLevelChanged_Should_Work()
    {
        var sw = new LoggingLevelSwitch();
        bool changed = false;
        sw.MinimumLevelChanged += (o, e) =>
        {
            Assert.Equal(sw, o);
            Assert.Equal(Information, e.OldLevel);
            Assert.Equal(Fatal, e.NewLevel);
            changed = true;
        };
        Assert.Equal(Information, sw.MinimumLevel);
        sw.MinimumLevel = Fatal;
        Assert.Equal(Fatal, sw.MinimumLevel);
        Assert.True(changed);
    }

    [Fact]
    public void DerivedSwitchCanProvideTheCurrentMinimumLevel()
    {
        var sw = new ScopedLevelSwitch(Information);
        Assert.Equal(Information, sw.MinimumLevel);

        using (ScopedLevelSwitch.ForScope(Debug))
        {
            Assert.Equal(Debug, sw.MinimumLevel);
        }

        Assert.Equal(Information, sw.MinimumLevel);
    }

    [Fact]
    public void DerivedSwitchControlsTheLoggerMinimumLevel()
    {
        var sink = new CollectingSink();
        var levelSwitch = new ScopedLevelSwitch(Information);
        var log = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .WriteTo.Sink(sink)
            .CreateLogger();

        log.Debug("Suppressed");
        log.Information("Emitted");

        using (ScopedLevelSwitch.ForScope(Debug))
        {
            log.Debug("Emitted");
        }

        log.Debug("Suppressed");

        Assert.Equal(["Emitted", "Emitted"], sink.Events.Select(e => e.RenderMessage()));
    }

    [Fact]
    public void DerivedSwitchControlsSinkRestrictions()
    {
        var sink = new CollectingSink();
        var levelSwitch = new ScopedLevelSwitch(Error);
        var log = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink, levelSwitch: levelSwitch)
            .CreateLogger();

        log.Warning("Suppressed");
        log.Error("Emitted");

        using (ScopedLevelSwitch.ForScope(Warning))
        {
            log.Warning("Emitted");
        }

        log.Warning("Suppressed");

        Assert.Equal(["Emitted", "Emitted"], sink.Events.Select(e => e.RenderMessage()));
    }

    [Fact]
    public void DerivedSwitchCanStillBeSetThroughTheBaseProperty()
    {
        var sw = new ScopedLevelSwitch(Information);
        LoggingLevelSwitch baseSwitch = sw;
        baseSwitch.MinimumLevel = Warning;
        Assert.Equal(Warning, sw.MinimumLevel);
    }

    sealed class ScopedLevelSwitch(LogEventLevel initialMinimumLevel) : LoggingLevelSwitch(initialMinimumLevel)
    {
        static readonly AsyncLocal<LogEventLevel?> CurrentLevel = new();

        public static IDisposable ForScope(LogEventLevel level)
        {
            var previous = CurrentLevel.Value;
            CurrentLevel.Value = level;
            return new Scope(previous);
        }

        public override LogEventLevel MinimumLevel => CurrentLevel.Value ?? base.MinimumLevel;

        sealed class Scope(LogEventLevel? previous) : IDisposable
        {
            public void Dispose() => CurrentLevel.Value = previous;
        }
    }
}
