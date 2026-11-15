namespace JesusKpekpes.Setup;

static class LaunchNote
{
    public static bool Blocked(Exception ex)
    {
        if (ex is System.ComponentModel.Win32Exception win && win.NativeErrorCode is 1260 or 4551) return true;
        return ex.Message.Contains("Application Control", StringComparison.OrdinalIgnoreCase);
    }

    public static string AfterInstall(Exception ex)
    {
        if (!Blocked(ex)) return "Installed, but it did not open. " + ex.Message + " It is in the Start menu. The audio was not changed.";
        return "Installed. Windows blocked the program from starting. It is already in the Start menu. Open Windows Security, then App and browser control. If Smart App Control is On, that policy blocked this file. Windows will not allow just this one program while that is On, and turning it off cannot be undone without resetting Windows. The audio was not changed.";
    }
}
