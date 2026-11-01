using SubZeroDev.WindowManager.Recovery;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// The host's journal for its session. Writes are serialised; every change reaches the disk before
/// <see cref="BeginAsync"/> returns (I6), and a failed write leaves the document as it was on disk.
/// </summary>
public sealed class RecoveryJournal : IRecoveryJournal
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private JournalDocument _doc;

    public RecoveryJournal(string path, int hostPid, DateTimeOffset hostStartTime)
    {
        _path = path;
        _doc = JournalDocument.Fresh(hostPid, hostStartTime);
    }

    public string Path => _path;

    public IReadOnlyList<DesktopChange> Entries => _doc.Entries;

    /// <summary>Writes the fresh document for this host: not a clean exit, no entries.</summary>
    public Task<Outcome> StartAsync(CancellationToken ct) => UpdateAsync(doc => doc, ct);

    public async Task<Outcome<Guid>> BeginAsync(DesktopChange change, CancellationToken ct)
    {
        var written = await UpdateAsync(doc => doc with { Entries = [.. doc.Entries, change] }, ct).ConfigureAwait(false);
        return written.Error is { } error ? Outcome<Guid>.Fail(error) : Outcome<Guid>.Success(change.Id);
    }

    public Task<Outcome> EndAsync(Guid changeId, CancellationToken ct) => UpdateAsync(doc => doc.Entries.Any(entry => entry.Id == changeId)
        ? doc with { Entries = [.. doc.Entries.Where(entry => entry.Id != changeId)] }
        : null, ct, $"No journal entry {changeId}.");

    public Task<Outcome> MarkCleanExitAsync(CancellationToken ct) => UpdateAsync(doc => doc with { CleanExit = true }, ct);

    /// <summary>Applies <paramref name="change"/> to the current document under the gate; null means there was nothing to change.</summary>
    private async Task<Outcome> UpdateAsync(Func<JournalDocument, JournalDocument?> change, CancellationToken ct, string? notFound = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return change(_doc) is { } next ? WriteLocked(next) : Outcome.Fail(CoreErrorCode.NotFound, notFound ?? "Nothing to change.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private Outcome WriteLocked(JournalDocument doc)
    {
        if (Journal.Write(_path, doc).Error is { } error)
        {
            return Outcome.Fail(CoreErrorCode.JournalWriteFailed, error.Detail, retryable: true);
        }

        _doc = doc;
        return Outcome.Success;
    }
}
