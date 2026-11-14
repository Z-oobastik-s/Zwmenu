using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Zwmenu.Services;

/// <summary>Настоящее меню проводника: свойства, открыть с помощью, расположение.</summary>
static class ShellContextMenu
{
    private const uint MfByPosition = 0x400;
    private const uint MfString = 0x0;
    private const uint MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x100;
    private const uint TpmRightButton = 0x2;
    private const uint CmfExplore = 0x4;
    private const uint CmfExtended = 0x100;
    private const uint BackId = 0x7FFE;
    private const uint LocateId = 0x7FFD;
    private const uint OpenWithId = 0x7FFC;
    private const int SwShow = 1;
    private const int WmInitMenuPopup = 0x0117;
    private const int WmMeasureItem = 0x002C;
    private const int WmDrawItem = 0x002B;
    private const int WmMenuChar = 0x0120;

    private static readonly Guid FolderId = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid MenuId = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid Menu2Id = new("000214F4-0000-0000-C000-000000000046");

    public static void Show(IntPtr owner, string path, bool extended, Action? putBack)
    {
        if (owner == IntPtr.Zero || string.IsNullOrWhiteSpace(path))
            return;
        IntPtr pidl = ILCreateFromPath(path);
        if (pidl == IntPtr.Zero)
            return;

        IntPtr folderPtr = IntPtr.Zero;
        IntPtr menuPtr = IntPtr.Zero;
        IntPtr menu2Ptr = IntPtr.Zero;
        IntPtr hmenu = IntPtr.Zero;
        HwndSource? source = HwndSource.FromHwnd(owner);
        IContextMenu2? relay = null;
        try
        {
            Guid folderId = FolderId;
            if (SHBindToParent(pidl, ref folderId, out folderPtr, out IntPtr child) != 0 || folderPtr == IntPtr.Zero)
                return;
            var folder = (IShellFolder)Marshal.GetTypedObjectForIUnknown(folderPtr, typeof(IShellFolder));
            Guid menuId = MenuId;
            if (folder.GetUIObjectOf(owner, 1, ref child, ref menuId, IntPtr.Zero, out menuPtr) != 0 || menuPtr == IntPtr.Zero)
                return;

            Guid menu2 = Menu2Id;
            if (Marshal.QueryInterface(menuPtr, ref menu2, out menu2Ptr) == 0 && menu2Ptr != IntPtr.Zero)
                relay = (IContextMenu2)Marshal.GetTypedObjectForIUnknown(menu2Ptr, typeof(IContextMenu2));
            IContextMenu commands = (IContextMenu)Marshal.GetTypedObjectForIUnknown(menuPtr, typeof(IContextMenu));

            hmenu = CreatePopupMenu();
            int custom = 0;
            if (putBack != null)
            {
                InsertMenu(hmenu, (uint)custom, MfByPosition | MfString, (UIntPtr)BackId, "На рабочий стол");
                custom++;
            }
            if (!path.StartsWith("::", StringComparison.Ordinal))
            {
                InsertMenu(hmenu, (uint)custom, MfByPosition | MfString, (UIntPtr)LocateId, "Расположение");
                custom++;
            }
            if (custom > 0)
                InsertMenu(hmenu, (uint)custom, MfByPosition | MfSeparator, UIntPtr.Zero, null);

            uint flags = CmfExplore;
            if (extended)
                flags |= CmfExtended;
            commands.QueryContextMenu(hmenu, custom > 0 ? (uint)custom + 1 : 0u, 1, 0x7000, flags);
            var verbs = new System.Collections.Generic.Dictionary<uint, string>();
            int rows = GetMenuItemCount(hmenu);
            for (int i = 0; i < rows; i++)
            {
                uint id = GetMenuItemID(hmenu, i);
                if (id == 0 || id >= 0x7000)
                    continue;
                string name = VerbName(commands, (int)id - 1);
                if (name.Length > 0)
                    verbs[id] = name;
            }
            OwnOpenWith(hmenu);

            HwndSourceHook? hook = null;
            if (source != null && relay != null)
            {
                hook = ForwardMenu;
                source.AddHook(hook);
            }

            IntPtr ForwardMenu(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
            {
                if (msg is WmInitMenuPopup or WmMeasureItem or WmDrawItem or WmMenuChar)
                    relay!.HandleMenuMsg((uint)msg, w, l);
                return IntPtr.Zero;
            }

            GetCursorPos(out Point pt);
            const int GwlExstyle = -20;
            const int WsExNoactivate = 0x08000000;
            int ex = GetWindowLong(owner, GwlExstyle);
            SetWindowLong(owner, GwlExstyle, ex & ~WsExNoactivate);
            SetForegroundWindow(owner);
            int cmd = 0;
            try
            {
                cmd = TrackPopupMenuEx(hmenu, TpmReturnCmd | TpmRightButton, pt.X, pt.Y, owner, IntPtr.Zero);
                if (cmd == 0)
                    return;
                if ((uint)cmd == OpenWithId || IsOpenWith(MenuLabel(hmenu, (uint)cmd)))
                {
                    ShowOpenWith(path);
                    return;
                }
                if ((uint)cmd == BackId)
            {
                putBack?.Invoke();
                return;
            }
            if ((uint)cmd == LocateId)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true })?.Dispose();
                return;
            }

            int offset = cmd - 1;
            verbs.TryGetValue((uint)cmd, out string? verb);
            verb = (verb ?? "").Trim();
            string label = MenuLabel(hmenu, (uint)cmd);
            if (string.Equals(verb, "open", StringComparison.OrdinalIgnoreCase) || label == "Открыть")
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
                return;
            }

            IntPtr verbPtr = IntPtr.Zero;
            IntPtr dirPtr = IntPtr.Zero;
            IntPtr infoPtr = IntPtr.Zero;
            try
            {
                verbPtr = string.IsNullOrEmpty(verb)
                    ? (IntPtr)offset
                    : Marshal.StringToHGlobalAnsi(verb);
                string? dir = path.StartsWith("::", StringComparison.Ordinal) ? null : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    dirPtr = Marshal.StringToHGlobalAnsi(dir);
                var info = new InvokeInfo
                {
                    Size = Marshal.SizeOf<InvokeInfo>(),
                    Window = owner,
                    Verb = verbPtr,
                    Directory = dirPtr,
                    Show = SwShow
                };
                infoPtr = Marshal.AllocHGlobal(info.Size);
                Marshal.StructureToPtr(info, infoPtr, false);
                int hr = commands.InvokeCommand(infoPtr);
                if (hr < 0 && string.Equals(verb, "explore", StringComparison.OrdinalIgnoreCase))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            finally
            {
                if (infoPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(infoPtr);
                if (!string.IsNullOrEmpty(verb) && verbPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(verbPtr);
                if (dirPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(dirPtr);
            }
            }
            finally
            {
                SetWindowLong(owner, GwlExstyle, ex);
                if (hook != null)
                    source!.RemoveHook(hook);
            }
        }
        finally
        {
            if (hmenu != IntPtr.Zero)
                DestroyMenu(hmenu);
            if (menu2Ptr != IntPtr.Zero)
                Marshal.Release(menu2Ptr);
            if (menuPtr != IntPtr.Zero)
                Marshal.Release(menuPtr);
            if (folderPtr != IntPtr.Zero)
                Marshal.Release(folderPtr);
            ILFree(pidl);
        }
    }

    private static void OwnOpenWith(IntPtr menu)
    {
        int count = GetMenuItemCount(menu);
        for (int i = 0; i < count; i++)
        {
            IntPtr sub = GetSubMenu(menu, i);
            if (!IsOpenWith(LabelAt(menu, i)))
            {
                if (sub != IntPtr.Zero)
                    OwnOpenWith(sub);
                continue;
            }

            RemoveMenu(menu, (uint)i, MfByPosition);
            if (sub != IntPtr.Zero)
                DestroyMenu(sub);
            InsertMenu(menu, (uint)i, MfByPosition | MfString, (UIntPtr)OpenWithId, "Открыть с помощью");
        }
    }

    private static bool IsOpenWith(string label) =>
        label.StartsWith("Открыть с помощью", StringComparison.OrdinalIgnoreCase);

    private static void ShowOpenWith(string path)
    {
        string exe = Path.Combine(Environment.SystemDirectory, "OpenWith.exe");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "\"" + path + "\"")
        {
            UseShellExecute = true
        })?.Dispose();
    }

    private static string LabelAt(IntPtr menu, int index)
    {
        var text = new System.Text.StringBuilder(260);
        GetMenuString(menu, (uint)index, text, text.Capacity, MfByPosition);
        return text.ToString().Replace("&", "").Trim();
    }

    private static string MenuLabel(IntPtr menu, uint id)
    {
        var text = new System.Text.StringBuilder(260);
        GetMenuString(menu, id, text, text.Capacity, 0);
        return text.ToString().Replace("&", "").Trim();
    }

    private static string VerbName(IContextMenu menu, int offset)
    {
        IntPtr buf = Marshal.AllocHGlobal(520);
        try
        {
            for (int i = 0; i < 520; i++)
                Marshal.WriteByte(buf, i, 0);
            if (menu.GetCommandString((UIntPtr)offset, 0, IntPtr.Zero, buf, 260) < 0)
                return "";
            return Marshal.PtrToStringAnsi(buf) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InvokeInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public IntPtr Verb;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon;
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr bc, string name, out uint eaten, out IntPtr pidl, ref uint attrs);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enm);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr bc, ref Guid iid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr bc, ref Guid iid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lparam, IntPtr a, IntPtr b);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid iid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint attrs);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint cidl, ref IntPtr apidl, ref Guid iid, IntPtr reserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, out IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, string name, uint flags, out IntPtr pidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(IntPtr info);
        [PreserveSig] int GetCommandString(UIntPtr cmd, uint kind, IntPtr reserved, IntPtr name, uint count);
    }

    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2 : IContextMenu
    {
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wparam, IntPtr lparam);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPath(string path);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid iid, out IntPtr folder, out IntPtr child);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenu(IntPtr menu, uint position, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr menu, uint item, System.Text.StringBuilder text, int max, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr menu, int position);

    [DllImport("user32.dll")]
    private static extern bool RemoveMenu(IntPtr menu, uint position, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
