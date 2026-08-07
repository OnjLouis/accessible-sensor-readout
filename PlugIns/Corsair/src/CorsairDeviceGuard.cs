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
            // Local copy: Dispose() can run concurrently on another thread and null out the field
            // between this check and the call below, but a local reference to the Mutex object stays
            // valid for WaitOne even after Dispose() clears mutex -- so the only race that matters is
            // whether this method reads mutex before or after it goes null, and both are handled.
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
