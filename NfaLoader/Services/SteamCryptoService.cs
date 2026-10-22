using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using NfaLoader.Localization;

namespace NfaLoader.Services;

internal sealed class SteamCryptoService
{
    private const int CryptprotectUiForbidden = 0x1;

    public string EncryptToHex(string token, string accountName)
    {
        var data = Encoding.UTF8.GetBytes(token);
        var entropy = Encoding.UTF8.GetBytes(accountName);
        var dataBlob = CreateBlob(data);
        var entropyBlob = CreateBlob(entropy);

        try
        {
            // Match the original binary: CRYPTPROTECT_UI_FORBIDDEN only (no AUDIT).
            var flags = CryptprotectUiForbidden;
            if (!CryptProtectData(
                    ref dataBlob,
                    "BObfuscateBuffer",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    flags,
                    out var protectedBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), Loc.T("Steam_Error_TokenEncryptFailed"));
            }

            try
            {
                var output = new byte[protectedBlob.cbData];
                Marshal.Copy(protectedBlob.pbData, output, 0, output.Length);
                return Convert.ToHexString(output).ToLowerInvariant();
            }
            finally
            {
                LocalFree(protectedBlob.pbData);
            }
        }
        finally
        {
            FreeBlob(dataBlob);
            FreeBlob(entropyBlob);
        }
    }

    /// <summary>Decrypts a saved login from local.vdf, or returns null when Windows cannot decrypt it for this account.</summary>
    public string? TryDecryptFromHex(string hex, string accountName)
    {
        byte[] data;
        try
        {
            data = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }

        var dataBlob = CreateBlob(data);
        var entropyBlob = CreateBlob(Encoding.UTF8.GetBytes(accountName));
        try
        {
            if (!CryptUnprotectData(
                    ref dataBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptprotectUiForbidden,
                    out var plainBlob))
            {
                return null;
            }

            try
            {
                var output = new byte[plainBlob.cbData];
                Marshal.Copy(plainBlob.pbData, output, 0, output.Length);
                return Encoding.UTF8.GetString(output);
            }
            finally
            {
                LocalFree(plainBlob.pbData);
            }
        }
        finally
        {
            FreeBlob(dataBlob);
            FreeBlob(entropyBlob);
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var blob = new DataBlob
        {
            cbData = data.Length,
            pbData = Marshal.AllocHGlobal(data.Length)
        };

        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }
}
