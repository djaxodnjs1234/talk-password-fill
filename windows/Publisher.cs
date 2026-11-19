using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TalkPasswordFill;

internal static class Publisher
{
    private static readonly Guid Authenticode = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static bool IsKakao(string path)
    {
        nint pathHandle = Marshal.StringToCoTaskMemUni(path);
        nint fileHandle = 0;
        var trust = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(),
            UiChoice = 2, // WTD_UI_NONE
            UnionChoice = 1, // WTD_CHOICE_FILE
            StateAction = 1, // WTD_STATEACTION_VERIFY
            ProviderFlags = 0x1000, // WTD_CACHE_ONLY_URL_RETRIEVAL
        };

        try
        {
            var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), FilePath = pathHandle };
            fileHandle = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
            Marshal.StructureToPtr(file, fileHandle, fDeleteOld: false);
            trust.File = fileHandle;
            if (WinVerifyTrust(-1, in Authenticode, ref trust) != 0) return false;

            nint provider = WTHelperProvDataFromStateData(trust.StateData);
            nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == 0) return false;
            var signerData = Marshal.PtrToStructure<ProviderSigner>(signer);
            if (signerData.CertificateCount == 0 || signerData.Certificates == 0) return false;
            var leaf = Marshal.PtrToStructure<ProviderCertificate>(signerData.Certificates);
            if (leaf.Certificate == 0) return false;
            var context = Marshal.PtrToStructure<CertificateContext>(leaf.Certificate);
            if (context.Encoded == 0 || context.EncodedLength is 0 or > 64 * 1024) return false;
            var data = new byte[context.EncodedLength];
            Marshal.Copy(context.Encoded, data, 0, data.Length);
            using var certificate = X509CertificateLoader.LoadCertificate(data);
            return certificate.SubjectName.EnumerateRelativeDistinguishedNames().Any(rdn =>
                !rdn.HasMultipleElements && rdn.GetSingleElementType().Value == "2.5.4.10" &&
                string.Equals(rdn.GetSingleElementValue(), "Kakao Corp.", StringComparison.OrdinalIgnoreCase));
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            if (trust.StateData != 0)
            {
                trust.StateAction = 2; // WTD_STATEACTION_CLOSE
                WinVerifyTrust(-1, in Authenticode, ref trust);
            }
            Marshal.FreeHGlobal(fileHandle);
            Marshal.FreeCoTaskMem(pathHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile
    {
        internal uint Size;
        internal nint FilePath;
        internal nint Handle;
        internal nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size;
        internal nint PolicyData;
        internal nint SipData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal nint File;
        internal uint StateAction;
        internal nint StateData;
        internal nint Url;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner
    {
        internal uint Size;
        internal FILETIME VerifyAsOf;
        internal uint CertificateCount;
        internal nint Certificates;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        internal uint Size;
        internal nint Certificate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        internal uint Encoding;
        internal nint Encoded;
        internal uint EncodedLength;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, in Guid action, ref TrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint index, bool counterSigner, uint counterSignerIndex);
}
