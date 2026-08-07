using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace SensorReadout.CorsairPlugIn
{
    public sealed class CorsairDeviceGuard : IDisposable
    {
        private Mutex mutex;

        public CorsairDeviceGuard()
        {
            try
            {
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    MutexRights.FullControl, AccessControlType.Allow));
                bool createdNew;
                mutex = new Mutex(false, "Global\\CorsairLinkReadWriteGuardMutex", out createdNew, security);
            }
            catch (UnauthorizedAccessException)
            {
                mutex = Mutex.OpenExisting("Global\\CorsairLinkReadWriteGuardMutex", MutexRights.Synchronize | MutexRights.Modify);
            }
        }

        public bool TryEnter(int timeoutMs)
        {
            // Local copy narrows the race with a concurrent Dispose(), but does not remove it: a
            // Mutex disposed after the copy still throws ObjectDisposedException from WaitOne, so
            // that is caught below and treated as "guard gone" (shutdown race only).
            var localMutex = mutex;
            if (localMutex == null)
            {
                return false;
            }

            try
            {
                return localMutex.WaitOne(timeoutMs);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Exit()
        {
            var localMutex = mutex;
            if (localMutex == null)
            {
                return;
            }

            try
            {
                localMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Swallow release errors (e.g., not owning the mutex)
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently during shutdown; nothing left to release.
            }
        }

        public void Dispose()
        {
            var localMutex = mutex;
            if (localMutex == null)
            {
                return;
            }

            mutex = null;
            localMutex.Dispose();
        }
    }
}
