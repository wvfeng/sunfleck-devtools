using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

// Acceptance helpers use the IDE's automation API. Screenshots target one explicit PID.
public static class SunfleckIdeAutomation
{
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr information);
        [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejection);
        [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
    }

    private sealed class BusyMessageFilter : IMessageFilter, IDisposable
    {
        private IMessageFilter previous;

        public BusyMessageFilter()
        {
            Marshal.ThrowExceptionForHR(CoRegisterMessageFilter(this, out previous));
        }

        public int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr information)
        {
            return 0;
        }

        public int RetryRejectedCall(IntPtr callee, int tickCount, int rejection)
        {
            return (rejection == 1 || rejection == 2) && tickCount < 10000 ? 100 : -1;
        }

        public int MessagePending(IntPtr callee, int tickCount, int pendingType)
        {
            return 2;
        }

        public void Dispose()
        {
            IMessageFilter ignored;
            Marshal.ThrowExceptionForHR(CoRegisterMessageFilter(previous, out ignored));
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(IMessageFilter replacement, out IMessageFilter previous);

    public static IDisposable RetryBusyCalls()
    {
        return new BusyMessageFilter();
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    public static object GetDte(int processId)
    {
        IRunningObjectTable table;
        IBindCtx context;
        Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out table));
        Marshal.ThrowExceptionForHR(CreateBindCtx(0, out context));
        IEnumMoniker enumerator = null;
        try
        {
            table.EnumRunning(out enumerator);
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                try
                {
                    string name;
                    monikers[0].GetDisplayName(context, null, out name);
                    if (name.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal) && name.EndsWith(":" + processId, StringComparison.Ordinal))
                    {
                        object dte;
                        table.GetObject(monikers[0], out dte);
                        return dte;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(monikers[0]);
                }
            }
            throw new InvalidOperationException("Visual Studio DTE is not registered for PID " + processId);
        }
        finally
        {
            if (enumerator != null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
            Marshal.ReleaseComObject(context);
            Marshal.ReleaseComObject(table);
        }
    }

    public static void Capture(int processId, string output)
    {
        using (var process = Process.GetProcessById(processId))
        {
            process.Refresh();
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero)
            {
                throw new InvalidOperationException("PID has no main window: " + processId);
            }
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != processId)
            {
                throw new InvalidOperationException("Window owner changed.");
            }
            Rect bounds;
            if (!GetWindowRect(window, out bounds))
            {
                throw new InvalidOperationException("Cannot read window bounds.");
            }
            using (var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var dc = graphics.GetHdc();
                bool captured;
                try
                {
                    captured = PrintWindow(window, dc, 2);
                }
                finally
                {
                    graphics.ReleaseHdc(dc);
                }
                if (!captured)
                {
                    throw new InvalidOperationException("PrintWindow failed for PID " + processId);
                }
                bitmap.Save(output, ImageFormat.Png);
            }
        }
    }
}
