using System.Threading;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private bool xagmanRelogDatabasePullPending;
    private long xagmanRelogDatabasePullGeneration;

    private void QueueXagmanRelogDatabasePull()
    {
        if (!isDisposed && xagmanRunning)
            xagmanRelogDatabasePullPending = true;
    }

    private void ResetXagmanRelogDatabasePull()
    {
        xagmanRelogDatabasePullPending = false;
        xagmanRelogDatabasePullGeneration++;
    }

    private void ProcessXagmanRelogDatabasePull()
    {
        if (isDisposed || !xagmanRunning || !xagmanRelogDatabasePullPending
            || Volatile.Read(ref xaDbPullRunning) != 0)
            return;

        // Wait for an earlier manual read to finish, then read committed rows afresh.
        // This never asks XA Database to collect/save an outgoing or missing character.
        xagmanRelogDatabasePullPending = false;
        var generation = xagmanRelogDatabasePullGeneration;
        bool IsCurrent() => !isDisposed && xagmanRunning && generation == xagmanRelogDatabasePullGeneration;
        PullXaDatabaseInfo(
            () => { if (IsCurrent()) ClearXagmanMatchingSelectionCaches(); },
            IsCurrent);
    }
}
