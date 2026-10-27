using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MacroInWZa007.Config;

/// <summary>
/// Encrypts text with Windows DPAPI for the current user, so a secret in config.json can only be
/// read by the same Windows account on the same PC.
/// </summary>
public static class Secrets
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    // Ties the ciphertext to this app, so other DPAPI users can't trivially decrypt it as theirs.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MacroInWZa007.Secret.v1");

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    public static string Protect(string text) =>
        Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(text), protect: true)!);

    public static bool TryUnprotect(string? protectedText, out string text)
    {
        text = "";
        if (string.IsNullOrEmpty(protectedText))
            return false;
        try
        {
            var bytes = Transform(Convert.FromBase64String(protectedText), protect: false);
            if (bytes is null)
                return false;
            text = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[]? Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var outBlob = new DataBlob();
            bool ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok)
                return null;
            try
            {
                var output = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, output, 0, outBlob.Size);
                return output;
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
