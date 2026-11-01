using System.Runtime.InteropServices;
using UTubeBulkUploader.Core;
using Windows.Storage;

namespace UTubeBulkUploader.Services;

/// <summary>
/// Reads "Media created" through the Windows property system (the same date File Explorer shows).
/// Called from a background thread while scanning.
/// </summary>
public sealed class ShellDateReader : IRecordingDateReader
{
    private static readonly string[] Props = { "System.Media.DateEncoded" };

    public DateTime? Read(string path)
    {
        try
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            var props = file.Properties.RetrievePropertiesAsync(Props).AsTask().GetAwaiter().GetResult();
            return props.TryGetValue("System.Media.DateEncoded", out var d) && d is DateTimeOffset dto ? dto.LocalDateTime : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read media properties of {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>The Windows folder picker with multi-select (IFileOpenDialog), which WinUI's FolderPicker lacks.</summary>
public static class MultiFolderPicker
{
    private const uint FosPickFolders = 0x20;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosAllowMultiSelect = 0x200;
    private const uint FosPathMustExist = 0x800;
    private const uint SigdnFileSysPath = 0x80058000;
    private const int Cancelled = unchecked((int)0x800704C7);

    public static List<string> Pick(IntPtr owner, string title)
    {
        var result = new List<string>();
        var dialog = (IFileOpenDialog)new FileOpenDialogRcw();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosAllowMultiSelect | FosPathMustExist);
            dialog.SetTitle(title);
            dialog.SetOkButtonLabel("Add folders");
            var hr = dialog.Show(owner);
            if (hr == Cancelled) return result;
            Marshal.ThrowExceptionForHR(hr);
            dialog.GetResults(out var items);
            items.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out var item);
                item.GetDisplayName(SigdnFileSysPath, out var ptr);
                var path = Marshal.PtrToStringUni(ptr);
                Marshal.FreeCoTaskMem(ptr);
                if (!string.IsNullOrEmpty(path)) result.Add(path);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
        return result;
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRcw { }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IShellItemArray ppenum);
        void GetSelectedItems(out IShellItemArray ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }
}

public static class Shell
{
    public static void Open(string target, string? arguments = null)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true };
            if (arguments is not null) psi.Arguments = arguments;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Log.Warn($"Could not open {target}: {ex.Message}"); }
    }
}
