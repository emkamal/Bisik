using System.Runtime.InteropServices;
using System.Text;

namespace Bisik.Core;

/// <summary>Owns the clipboard lock across sequence checks and writes, so restoration cannot erase a newer copy.</summary>
internal sealed class WindowsClipboard : NativeWindow, IDisposable
{
    private const uint UnicodeText = 13;
    private const int MaximumSnapshotBytes = 32 * 1024 * 1024;
    private static readonly IClipboardOwnershipAccess OwnershipAccess = new NativeOwnershipAccess();

    public WindowsClipboard() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
    public static uint Sequence => GetClipboardSequenceNumber();

    public ClipboardSnapshot? Capture()
    {
        if (!OpenClipboard(Handle)) return null;
        try
        {
            var formats = new List<ClipboardFormat>();
            long total = 0;
            for (uint format = 0; (format = EnumClipboardFormats(format)) != 0;)
            {
                // Opaque global-memory formats cover Unicode, RTF, HTML, DIB images and file lists.
                // GDI handles and owner-rendered formats need different lifetimes: leave them untouched.
                if (format is 2 or 3 or 9 or 14 or 0x80) return null;
                var handle = GetClipboardData(format);
                var size = (long)GlobalSize(handle);
                if (size <= 0 || (total += size) > MaximumSnapshotBytes) return null;
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) return null;
                try
                {
                    var bytes = new byte[(int)size];
                    Marshal.Copy(pointer, bytes, 0, bytes.Length);
                    formats.Add(new(format, bytes));
                }
                finally { GlobalUnlock(handle); }
            }
            return new(Sequence, formats);
        }
        finally { CloseClipboard(); }
    }

    public uint? TrySetText(string text, uint expectedSequence)
    {
        var formats = new[] { new ClipboardFormat(UnicodeText, Encoding.Unicode.GetBytes(text + '\0')) };
        return TryWrite(formats, expectedSequence);
    }

    public bool TryRestore(ClipboardSnapshot snapshot, uint expectedSequence) => TryWrite(snapshot.Formats, expectedSequence) is not null;

    private uint? TryWrite(IReadOnlyList<ClipboardFormat> formats, uint expectedSequence)
    {
        var allocated = new List<(uint Format, IntPtr Handle)>();
        try
        {
            // Allocate before emptying the clipboard. Allocation failure must not destroy its contents.
            foreach (var format in formats)
            {
                var handle = GlobalAlloc(0x0002, (UIntPtr)format.Data.Length);
                if (handle == IntPtr.Zero) return null;
                allocated.Add((format.Id, handle));
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) return null;
                try { Marshal.Copy(format.Data, 0, pointer, format.Data.Length); }
                finally { GlobalUnlock(handle); }
            }

            if (!OpenClipboard(Handle)) return null;
            var mustClose = true;
            try
            {
                if (Sequence != expectedSequence || !EmptyClipboard()) return null;
                for (var index = 0; index < allocated.Count; index++)
                {
                    var item = allocated[index];
                    if (SetClipboardData(item.Format, item.Handle) == IntPtr.Zero) return null;
                    allocated[index] = (item.Format, IntPtr.Zero); // Windows owns this allocation now.
                }
                mustClose = false;
                return CompleteWrite(Handle, OwnershipAccess);
            }
            finally { if (mustClose) CloseClipboard(); }
        }
        finally
        {
            foreach (var item in allocated)
                if (item.Handle != IntPtr.Zero) GlobalFree(item.Handle);
        }
    }

    internal static uint? CompleteWrite(IntPtr owner, IClipboardOwnershipAccess access)
    {
        // CloseClipboard can publish synthesized text formats and advance the revision. Read the
        // receipt afterward, under a fresh lock, without adopting a competing application's copy.
        if (!access.Close() || !access.Open(owner)) return null;
        try { return access.Owner == owner ? access.Sequence : null; }
        finally { access.Close(); }
    }

    private sealed class NativeOwnershipAccess : IClipboardOwnershipAccess
    {
        public bool Open(IntPtr owner) => OpenClipboard(owner);
        public bool Close() => CloseClipboard();
        public IntPtr Owner => GetClipboardOwner();
        public uint Sequence => GetClipboardSequenceNumber();
    }

    public void Dispose() => DestroyHandle();

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}

internal interface IClipboardOwnershipAccess
{
    bool Open(IntPtr owner);
    bool Close();
    IntPtr Owner { get; }
    uint Sequence { get; }
}
