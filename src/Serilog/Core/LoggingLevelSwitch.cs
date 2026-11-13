// Copyright 2013-2015 Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Serilog.Core;

/// <summary>
/// Dynamically controls logging level.
/// </summary>
public class LoggingLevelSwitch
{
    volatile LogEventLevel _minimumLevel;
    readonly object _levelUpdateLock = new();

    /// <summary>
    /// Create a <see cref="LoggingLevelSwitch"/> at the initial
    /// minimum level.
    /// </summary>
    /// <param name="initialMinimumLevel">The initial level to which the switch is set.</param>
    public LoggingLevelSwitch(LogEventLevel initialMinimumLevel = LogEventLevel.Information)
    {
        _minimumLevel = initialMinimumLevel;
    }

    /// <summary>
    /// The event arises when <see cref="MinimumLevel"/> changed. Note that the event is raised
    /// under a lock so be careful within event handler to not fall into deadlock.
    /// </summary>
    public event EventHandler<LoggingLevelSwitchChangedEventArgs>? MinimumLevelChanged;

    /// <summary>
    /// The current minimum level, below which no events
    /// should be generated.
    /// </summary>
    /// <remarks>
    /// This property is virtual so that derived switches can provide a level that varies
    /// with ambient state, such as the current tenant or request. A derived switch can be
    /// used anywhere a <see cref="LoggingLevelSwitch"/> is accepted, including
    /// <c>MinimumLevel.ControlledBy()</c> and the level switch parameters of the sink and
    /// enricher configuration methods.
    /// </remarks>
    // Reading this property generates a memory barrier,
    // so needs to be used judiciously in the logging pipeline.
    public virtual LogEventLevel MinimumLevel
    {
        get { return _minimumLevel; }
        set
        {
            lock (_levelUpdateLock)
            {
                var old = _minimumLevel;
                if (old != value)
                {
                    _minimumLevel = value;
                    MinimumLevelChanged?.Invoke(this, new LoggingLevelSwitchChangedEventArgs(old, value));
                }
            }
        }
    }
}
