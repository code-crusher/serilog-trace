// Copyright © Serilog Contributors
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
/// Implemented by sinks that buffer events and can write them to their destination on demand. Flushing
/// is non-destructive: the sink remains open, and logging can continue afterwards.
/// </summary>
/// <remarks>The Serilog pipeline forwards flush requests to all of the sinks that implement this
/// interface, when <see cref="Logger.FlushAsync"/> or <see cref="Serilog.Log.FlushAsync"/> is called.
/// Flushing is best-effort: sinks that are unable to write buffered events should report the failure
/// through <see cref="ISetLoggingFailureListener"/> or <see cref="Serilog.Debugging.SelfLog"/>, rather
/// than throwing.</remarks>
public interface IFlushableLogEventSink
{
    /// <summary>
    /// Write any buffered log events to the sink's destination.
    /// </summary>
    /// <returns>A task that completes when buffered events have been written, or when the attempt to
    /// write them has failed.</returns>
    Task FlushAsync();
}
