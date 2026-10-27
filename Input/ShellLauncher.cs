using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MacroInWZa007.Input;

/// <summary>
/// Starts programs without admin rights even when Macro itself runs as admin, by asking the
/// desktop's Explorer (which runs as the normal user) to open them. Otherwise anything able to
/// edit config.json could get its programs run as admin through us.
/// </summary>
internal static class ShellLauncher
{
    private const int CSIDL_DESKTOP = 0;
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const int SVGIO_BACKGROUND = 0;
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    public static bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Opens a program, file, folder, or URL as the signed-in user, never elevated.</summary>
    public static void OpenAsUser(string file, string arguments)
    {
        dynamic shellWindows = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid)!)!;
        try
        {
            object location = CSIDL_DESKTOP;
            object empty = Type.Missing;
            object desktop = shellWindows.FindWindowSW(ref location, ref empty, SWC_DESKTOP, out int hwnd, SWFO_NEEDDISPATCH);
            var browser = (IShellBrowser)((IServiceProvider)desktop).QueryService(
                SID_STopLevelBrowser, typeof(IShellBrowser).GUID);
            browser.QueryActiveShellView(out var view);
            view.GetItemObject(SVGIO_BACKGROUND, typeof(IDispatch).GUID, out object folderView);
            dynamic shell = ((dynamic)folderView).Application;
            shell.ShellExecute(file, arguments, "", "open", 1);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellWindows);
        }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService([In] in Guid service, [In] in Guid riid);
    }

    [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IDispatch
    {
    }

    // Only QueryActiveShellView is called; the methods before it just keep the vtable slots in order.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        void QueryActiveShellView(out IShellView view);
    }

    // Only GetItemObject is called; the methods before it just keep the vtable slots in order.
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();
        void GetItemObject(uint item, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
}
