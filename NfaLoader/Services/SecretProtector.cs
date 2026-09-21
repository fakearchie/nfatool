using System.Runtime.InteropServices;
using System.Text;

namespace NfaLoader.Services;

/// <summary>
/// Windows DPAPI, scoped to the current user. A settings file copied to another machine or another
/// Windows account cannot be decrypted, which matters for a key that spends real balance.
/// </summary>
internal static partial class SecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    // Entropy ties the blob to this purpose, so it cannot be swapped for another DPAPI blob of ours.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("nfa.pub Loader API key v1");

    public static string Protect(string plainText)
    {
        var data = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(Transform(data, protect: true));
    }

    /// <summary>Returns null when the blob cannot be decrypted, which is how a foreign or corrupt value shows.</summary>
    public static string? TryUnprotect(string? protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return null;
        }

        try
        {
            var data = Convert.FromBase64String(protectedText);
            return Encoding.UTF8.GetString(Transform(data, protect: false));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { cbData = input.Length, pbData = inputHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };

            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob);

            if (!ok)
            {
                throw new InvalidOperationException($"DPAPI failed with error {Marshal.GetLastPInvokeError()}.");
            }

            try
            {
                var output = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, output, 0, outBlob.cbData);
                return output;
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);
}
