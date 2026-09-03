using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace JenkinsTray.Services;

/// <summary>
/// Windows only displays toasts from a desktop app whose AppUserModelID is backed by a Start-menu
/// shortcut carrying that ID and the toast activator CLSID. The notifications library registers the
/// ID and the COM server in the registry but never creates the shortcut, so toasts are accepted and
/// then silently dropped. This creates the missing shortcut, reusing the identifiers the library
/// just wrote to the registry so activation (click → open the job) keeps working.
/// </summary>
internal static class ToastShortcut
{
    private const string ShortcutFileName = "Jenkins Tray.lnk";

    // System.AppUserModel.ID and System.AppUserModel.ToastActivatorCLSID
    private static readonly Guid AppUserModelFormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint AppUserModelIdPropertyId = 5;
    private const uint ToastActivatorClsidPropertyId = 26;

    public static void Ensure()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return;

        try
        {
            var registration = FindRegistration(exePath);
            if (registration is null)
            {
                AppLog.Info("No notification registration found in the registry; shortcut skipped.");
                return;
            }

            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs",
                ShortcutFileName);

            if (File.Exists(shortcutPath) && IsUsable(shortcutPath, exePath, registration.Value.Aumid))
                return;

            Create(shortcutPath, exePath, registration.Value.Aumid, registration.Value.Clsid);
            AppLog.Info($"Notification shortcut created: {shortcutPath}");
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn("Creating the notification shortcut", ex);
        }
    }

    /// <summary>
    /// Finds the AUMID whose custom activator points back at our executable — that is the identity
    /// the notifications library uses when it sends a toast.
    /// </summary>
    private static (string Aumid, Guid Clsid)? FindRegistration(string exePath)
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId");
        if (root is null)
            return null;

        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);

            if (key?.GetValue("CustomActivator") is not string clsidText)
                continue;

            if (!Guid.TryParse(clsidText, out var clsid))
                continue;

            using var server = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{clsid:D}}}\LocalServer32");
            if (server?.GetValue(null) is not string command)
                continue;

            if (command.Contains(exePath, StringComparison.OrdinalIgnoreCase))
                return (name, clsid);
        }

        return null;
    }

    /// <summary>
    /// A shortcut is only good enough if it points at this executable <em>and</em> carries the
    /// AppUserModelID. An installer-created shortcut has the right target but no identity, and
    /// Windows would then drop every toast — so that case must be repaired, not accepted.
    /// </summary>
    private static bool IsUsable(string shortcutPath, string exePath, string aumid)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(shortcutPath, 0);

            var buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);

            if (!string.Equals(buffer.ToString(), exePath, StringComparison.OrdinalIgnoreCase))
                return false;

            return string.Equals(ReadAumid((IPropertyStore)link), aumid, StringComparison.Ordinal);
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static string? ReadAumid(IPropertyStore store)
    {
        var key = new PropertyKey { FormatId = AppUserModelFormatId, PropertyId = AppUserModelIdPropertyId };
        store.GetValue(ref key, out var variant);

        try
        {
            return variant.ValueType == VT_LPWSTR ? Marshal.PtrToStringUni(variant.Value) : null;
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    private static void Create(string shortcutPath, string exePath, string aumid, Guid activatorClsid)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        var link = (IShellLinkW)new ShellLinkCoClass();
        link.SetPath(exePath);
        link.SetArguments("--minimized");
        link.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
        link.SetIconLocation(exePath, 0);
        link.SetDescription("Surveillance des builds Jenkins");

        var store = (IPropertyStore)link;
        SetString(store, AppUserModelIdPropertyId, aumid);
        SetClsid(store, ToastActivatorClsidPropertyId, activatorClsid);
        store.Commit();

        ((IPersistFile)link).Save(shortcutPath, true);
    }

    // The InitPropVariantFrom* helpers are inline header functions, not exports, so the variants
    // are built by hand. PropVariantClear frees the CoTaskMem allocations for both value types.
    private const ushort VT_LPWSTR = 31;
    private const ushort VT_CLSID = 72;

    private static void SetString(IPropertyStore store, uint propertyId, string value)
    {
        var key = new PropertyKey { FormatId = AppUserModelFormatId, PropertyId = propertyId };
        var variant = new PropVariant { ValueType = VT_LPWSTR, Value = Marshal.StringToCoTaskMemUni(value) };

        try
        {
            store.SetValue(ref key, ref variant);
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    private static void SetClsid(IPropertyStore store, uint propertyId, Guid value)
    {
        var key = new PropertyKey { FormatId = AppUserModelFormatId, PropertyId = propertyId };

        var buffer = Marshal.AllocCoTaskMem(16);
        Marshal.Copy(value.ToByteArray(), 0, buffer, 16);

        var variant = new PropVariant { ValueType = VT_CLSID, Value = buffer };

        try
        {
            store.SetValue(ref key, ref variant);
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    // ---------------------------------------------------------------- interop

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void PropVariantClear(ref PropVariant variant);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort ValueType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Value;
        public IntPtr Value2;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellLinkCoClass;

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxLength, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxLength);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxLength);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxLength);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
