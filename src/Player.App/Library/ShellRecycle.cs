using System.Runtime.InteropServices;

namespace Player.App.Library;

// Windows owns recycling, progress, cancellation, and any filesystem prompts.
// Use a dedicated STA so a large folder operation never blocks XAML layout.
internal static class ShellRecycle
{
    internal static Task<bool> SendAsync(string path, nint owner)
    {
        path = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || path.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a file or folder, not a drive root.");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? operationObject = null;
            IShellItem? item = null;
            try
            {
                operationObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!);
                var operation = (IFileOperation)operationObject!;
                Guid shellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, in shellItemId, out item));
                operation.SetOwnerWindow(owner);
                // Recycle, record Explorer undo, and retain Windows' warning if
                // a device/location cannot recycle. Never auto-accept prompts.
                operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00004000);
                operation.DeleteItem(item, 0);
                operation.PerformOperations();
                operation.GetAnyOperationsAborted(out bool aborted);
                completion.TrySetResult(!aborted);
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x800704C7)) { completion.TrySetResult(false); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally
            {
                if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                if (operationObject is not null && Marshal.IsComObject(operationObject)) Marshal.ReleaseComObject(operationObject);
            }
        }) { IsBackground = true, Name = "mpv.win recycle operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint binding, in Guid id,
        out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, in Guid handler, in Guid id, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint nameType, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    // Keep the complete native vtable order, including unused slots.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(nint sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] object item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.Interface)] object destination);
        void CopyItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.Interface)] object destination);
        void DeleteItem(IShellItem item, nint sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem([MarshalAs(UnmanagedType.Interface)] object destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, nint sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
