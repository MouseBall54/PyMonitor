using System.IO;
using System.Runtime.InteropServices;

namespace PyRuntimeInspector.App.Services;

public interface IAuthenticodeVerifier
{
    void VerifyTrusted(string filePath);
}

public sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new(
        0x00AAC56B,
        0xCD44,
        0x11D0,
        0x8C,
        0xC2,
        0x00,
        0xC0,
        0x4F,
        0xC2,
        0x95,
        0xEE);

    public void VerifyTrusted(string filePath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
        if (!File.Exists(filePath))
            throw new FileNotFoundException("The installer to verify was not found.", filePath);

        var fullPath = Path.GetFullPath(filePath);
        var pathPointer = Marshal.StringToCoTaskMemUni(fullPath);
        var fileInfoPointer = IntPtr.Zero;
        var trustData = new WinTrustData();
        var action = GenericVerifyV2;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            trustData = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WinTrustUiChoice.None,
                RevocationChecks = WinTrustRevocationChecks.WholeChain,
                UnionChoice = WinTrustUnionChoice.File,
                FileInfo = fileInfoPointer,
                StateAction = WinTrustStateAction.Verify,
                ProviderFlags = WinTrustProviderFlags.RevocationCheckChainExcludeRoot
                    | WinTrustProviderFlags.DisableMd2Md4,
            };

            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            if (status != 0)
                throw new ApplicationUpdateException(
                    "AUTHENTICODE_INVALID",
                    $"Windows did not trust the MSI Authenticode signature (0x{unchecked((uint)status):X8}).");
        }
        finally
        {
            if (trustData.StateData != IntPtr.Zero)
            {
                trustData.StateAction = WinTrustStateAction.Close;
                _ = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            }
            if (fileInfoPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public WinTrustUiChoice UiChoice;
        public WinTrustRevocationChecks RevocationChecks;
        public WinTrustUnionChoice UnionChoice;
        public IntPtr FileInfo;
        public WinTrustStateAction StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public WinTrustProviderFlags ProviderFlags;
        public uint UiContext;
    }

    private enum WinTrustUiChoice : uint
    {
        None = 2,
    }

    private enum WinTrustRevocationChecks : uint
    {
        WholeChain = 1,
    }

    private enum WinTrustUnionChoice : uint
    {
        File = 1,
    }

    private enum WinTrustStateAction : uint
    {
        Verify = 1,
        Close = 2,
    }

    [Flags]
    private enum WinTrustProviderFlags : uint
    {
        RevocationCheckChainExcludeRoot = 0x00000080,
        DisableMd2Md4 = 0x00002000,
    }
}
