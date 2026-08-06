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
            try
            {
                return mutex.WaitOne(timeoutMs);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        public void Exit()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Swallow release errors (e.g., not owning the mutex)
            }
        }

        public void Dispose()
        {
            if (mutex != null)
            {
                mutex.Dispose();
                mutex = null;
            }
        }
    }
}
