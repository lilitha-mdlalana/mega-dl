using MegaDl.Core.Links;

namespace MegaDl.Core.Transfers;

public enum TransferState { Queued, Starting, Running, Paused, Verifying, Completed, Failed }

/// <summary>
/// An immutable point-in-time view of one transfer. Cheap to copy, safe to hand to the UI thread.
///
/// <paramref name="GroupId"/>/<paramref name="GroupName"/>/<paramref name="RelativePath"/> are
/// set by the engine when a transfer came from expanding a folder link (Phase 2c): every file
/// from the same folder link shares one <see cref="GroupId"/>, so the UI can fold them into one
/// collapsible section. A plain file link leaves all three null — the UI buckets those under a
/// shared "Single files" section instead of grouping them individually.
/// </summary>
public sealed record TransferSnapshot(
    Guid Id,
    string Name,
    long Size,
    long BytesDone,
    double BytesPerSecond,
    TransferState State,
    string? Error = null,
    string? GroupId = null,
    string? GroupName = null,
    string? RelativePath = null);

/// <summary>
/// The seam between engine and UI. The UI never gets callbacks from worker threads;
/// it calls <see cref="Snapshot"/> on a timer (pull model) and renders what it gets.
///
/// Today: SimulatedTransferEngine (Desktop project) implements this.
/// Phase 3: the real TransferManager (port of TransferenceManager.java) implements it,
/// and the UI doesn't change.
/// </summary>
public interface ITransferEngine
{
    Guid Enqueue(MegaLink link, string destinationDirectory);
    void Pause(Guid id);
    void Resume(Guid id);
    void Cancel(Guid id);
    IReadOnlyList<TransferSnapshot> Snapshot();
}
