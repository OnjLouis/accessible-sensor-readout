using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class RemoteFirewallManager
{
    internal const string RuleName = "Sensor Readout Remote Monitoring";
    private const int DomainAndPrivateProfiles = 1 | 2;
    private const int InboundDirection = 1;
    private const int AllowAction = 1;
    private const int TcpProtocol = 6;

    internal static bool TryEnsureInboundRule(int port, out string error)
    {
        error = "";
        if (port < 1024 || port > 65535)
        {
            error = "The server port is outside the valid range.";
            return false;
        }

        object policy = null;
        object rules = null;
        object rule = null;
        try
        {
            policy = CreateComObject("HNetCfg.FwPolicy2");
            rules = GetProperty(policy, "Rules");
            TryRemoveRule(rules);

            rule = CreateComObject("HNetCfg.FWRule");
            SetProperty(rule, "Name", RuleName);
            SetProperty(rule, "Description", "Allows password-protected Sensor Readout remote monitoring on trusted networks.");
            SetProperty(rule, "Grouping", "Sensor Readout");
            SetProperty(rule, "Protocol", TcpProtocol);
            SetProperty(rule, "LocalPorts", port.ToString(CultureInfo.InvariantCulture));
            SetProperty(rule, "Direction", InboundDirection);
            SetProperty(rule, "Profiles", DomainAndPrivateProfiles);
            SetProperty(rule, "EdgeTraversal", false);
            SetProperty(rule, "Action", AllowAction);
            SetProperty(rule, "Enabled", true);
            InvokeMethod(rules, "Add", rule);
            return true;
        }
        catch (Exception exception)
        {
            error = UnwrapException(exception).Message;
            return false;
        }
        finally
        {
            ReleaseComObject(rule);
            ReleaseComObject(rules);
            ReleaseComObject(policy);
        }
    }

    internal static bool TryRemoveInboundRule(out string error)
    {
        error = "";
        object policy = null;
        object rules = null;
        try
        {
            policy = CreateComObject("HNetCfg.FwPolicy2");
            rules = GetProperty(policy, "Rules");
            TryRemoveRule(rules);
            return true;
        }
        catch (Exception exception)
        {
            error = UnwrapException(exception).Message;
            return false;
        }
        finally
        {
            ReleaseComObject(rules);
            ReleaseComObject(policy);
        }
    }

    private static object CreateComObject(string programId)
    {
        var type = Type.GetTypeFromProgID(programId);
        if (type == null)
        {
            throw new InvalidOperationException("Windows Firewall management is not available.");
        }
        return Activator.CreateInstance(type);
    }

    private static void TryRemoveRule(object rules)
    {
        try
        {
            InvokeMethod(rules, "Remove", RuleName);
        }
        catch (TargetInvocationException exception)
        {
            var comError = exception.InnerException as COMException;
            if (comError == null || comError.ErrorCode != unchecked((int)0x80070002))
            {
                throw;
            }
        }
        catch (COMException exception)
        {
            if (exception.ErrorCode != unchecked((int)0x80070002))
            {
                throw;
            }
        }
    }

    private static object GetProperty(object target, string name)
    {
        return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);
    }

    private static void SetProperty(object target, string name, object value)
    {
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value }, CultureInfo.InvariantCulture);
    }

    private static object InvokeMethod(object target, string name, params object[] arguments)
    {
        return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments, CultureInfo.InvariantCulture);
    }

    private static Exception UnwrapException(Exception exception)
    {
        while (exception is TargetInvocationException && exception.InnerException != null)
        {
            exception = exception.InnerException;
        }
        return exception;
    }

    private static void ReleaseComObject(object value)
    {
        if (value == null || !Marshal.IsComObject(value))
        {
            return;
        }
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }
}
