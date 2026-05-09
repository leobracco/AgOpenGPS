using AgOpenGPS.AgroParallel.Core.Commands;
using AgOpenGPS.AgroParallel.Core.Runtime;

namespace AgOpenGPS.AgroParallel.Core.Bridge;

/// <summary>
/// Boundary between the legacy AgOpenGPS engine and any visual layer.
/// Implementations can wrap FormGPS today and a headless engine later.
/// </summary>
public interface IAgpEngineBridge
{
    AgpRuntimeState GetSnapshot();
    AgpCommandResult Execute(AgpCommand command);
}

public interface IAgpStatePublisher
{
    event EventHandler<AgpRuntimeState>? SnapshotPublished;
    void Start();
    void Stop();
}

public interface IAgpCommandDispatcher
{
    AgpCommandResult Dispatch(AgpCommand command);
}
