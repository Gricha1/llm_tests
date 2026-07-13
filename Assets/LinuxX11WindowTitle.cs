using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
/// <summary>
/// Переименование окон текущего процесса через X11 (_NET_WM_PID), без xdotool.
/// </summary>
public static class LinuxX11WindowTitle
{
    const string LibX11 = "libX11.so.6";
    const int Success = 0;
    const int PropModeReplace = 0;
    const int XA_CARDINAL = 6;

    [DllImport(LibX11)]
    static extern IntPtr XOpenDisplay(string displayName);

    [DllImport(LibX11)]
    static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    static extern IntPtr XInternAtom(IntPtr display, string atomName, bool onlyIfExists);

    [DllImport(LibX11)]
    static extern int XStoreName(IntPtr display, IntPtr window, string windowName);

    [DllImport(LibX11)]
    static extern int XSync(IntPtr display, bool discardEvents);

    [DllImport(LibX11)]
    static extern int XFree(IntPtr data);

    [DllImport(LibX11)]
    static extern int XQueryTree(
        IntPtr display,
        IntPtr window,
        out IntPtr rootReturn,
        out IntPtr parentReturn,
        out IntPtr childrenReturn,
        out uint nChildrenReturn);

    [DllImport(LibX11)]
    static extern int XGetWindowProperty(
        IntPtr display,
        IntPtr window,
        IntPtr property,
        long offset,
        long length,
        bool delete,
        IntPtr reqType,
        out IntPtr actualTypeReturn,
        out int actualFormatReturn,
        out ulong nItemsReturn,
        out ulong bytesAfterReturn,
        out IntPtr propReturn);

    [DllImport(LibX11)]
    static extern int XChangeProperty(
        IntPtr display,
        IntPtr window,
        IntPtr property,
        IntPtr type,
        int format,
        int mode,
        byte[] data,
        int nelements);

    public static bool TrySetForCurrentProcess(string title)
    {
        if (string.IsNullOrEmpty(title))
            return false;

        IntPtr display = XOpenDisplay(null);
        if (display == IntPtr.Zero)
            return false;

        try
        {
            int pid = Process.GetCurrentProcess().Id;
            IntPtr pidAtom = XInternAtom(display, "_NET_WM_PID", false);
            IntPtr netWmNameAtom = XInternAtom(display, "_NET_WM_NAME", false);
            IntPtr utf8Atom = XInternAtom(display, "UTF8_STRING", false);
            IntPtr root = XDefaultRootWindow(display);
            int renamed = RenameMatchingWindows(display, root, pidAtom, netWmNameAtom, utf8Atom, pid, title);
            XSync(display, false);
            return renamed > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    static int RenameMatchingWindows(
        IntPtr display,
        IntPtr window,
        IntPtr pidAtom,
        IntPtr netWmNameAtom,
        IntPtr utf8Atom,
        int pid,
        string title)
    {
        int renamed = 0;
        if (TryGetWindowPid(display, window, pidAtom, out int windowPid) && windowPid == pid)
        {
            XStoreName(display, window, title);
            SetNetWmName(display, window, netWmNameAtom, utf8Atom, title);
            renamed++;
        }

        if (XQueryTree(display, window, out _, out _, out IntPtr children, out uint nChildren) != Success)
            return renamed;

        if (nChildren == 0 || children == IntPtr.Zero)
            return renamed;

        try
        {
            int stride = IntPtr.Size;
            for (uint i = 0; i < nChildren; i++)
            {
                IntPtr child = Marshal.ReadIntPtr(children, (int)(i * stride));
                if (child != IntPtr.Zero)
                    renamed += RenameMatchingWindows(display, child, pidAtom, netWmNameAtom, utf8Atom, pid, title);
            }
        }
        finally
        {
            XFree(children);
        }

        return renamed;
    }

    static bool TryGetWindowPid(IntPtr display, IntPtr window, IntPtr pidAtom, out int pid)
    {
        pid = 0;
        int status = XGetWindowProperty(
            display,
            window,
            pidAtom,
            0,
            1,
            false,
            new IntPtr(XA_CARDINAL),
            out IntPtr actualType,
            out int actualFormat,
            out ulong nItems,
            out _,
            out IntPtr prop);

        if (status != Success || nItems != 1 || prop == IntPtr.Zero)
            return false;

        try
        {
            pid = Marshal.ReadInt32(prop);
            return true;
        }
        finally
        {
            XFree(prop);
        }
    }

    static void SetNetWmName(IntPtr display, IntPtr window, IntPtr netWmNameAtom, IntPtr utf8Atom, string title)
    {
        if (netWmNameAtom == IntPtr.Zero || utf8Atom == IntPtr.Zero)
            return;

        byte[] bytes = Encoding.UTF8.GetBytes(title);
        XChangeProperty(display, window, netWmNameAtom, utf8Atom, 8, PropModeReplace, bytes, bytes.Length);
    }
}
#else
public static class LinuxX11WindowTitle
{
    public static bool TrySetForCurrentProcess(string title) => false;
}
#endif
