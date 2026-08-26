using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EnviousWispr.Core.Distribution;

namespace EnviousWispr.Services.Distribution;

public sealed class WindowsUpdateArtifactValidator : IUpdateArtifactValidator
{
    private const uint WinTrustNoUi = 2;
    private const uint WinTrustFileChoice = 1;
    private const uint WinTrustCacheOnlyUrlRetrieval = 0x00001000;

    private static readonly Guid GenericVerifyV2 = new(
        "00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public async Task<UpdateArtifactAdmissionResult> ValidateAsync(
        UpdateArtifactAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var artifactPath = Path.GetFullPath(admission.ArtifactPath);
        if (!File.Exists(artifactPath) ||
            string.IsNullOrWhiteSpace(admission.ExpectedSha256) ||
            string.IsNullOrWhiteSpace(admission.RequiredPublisherSubject))
        {
            return new UpdateArtifactAdmissionResult(UpdateOperationStatus.Failed);
        }

        var expectedPrefix = admission.Identity.PackageId + "-";
        if (!Path.GetFileName(artifactPath).StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return new UpdateArtifactAdmissionResult(UpdateOperationStatus.RejectedChannel);
        }

        string actualHash;
        try
        {
            await using var stream = new FileStream(
                artifactPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (IOException)
        {
            return new UpdateArtifactAdmissionResult(UpdateOperationStatus.Failed);
        }
        catch (UnauthorizedAccessException)
        {
            return new UpdateArtifactAdmissionResult(UpdateOperationStatus.Failed);
        }
        catch (SecurityException)
        {
            return new UpdateArtifactAdmissionResult(UpdateOperationStatus.Failed);
        }

        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(NormalizeSha256(admission.ExpectedSha256));
        }
        catch (ArgumentException)
        {
            return new UpdateArtifactAdmissionResult(UpdateOperationStatus.Failed, actualHash);
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedHash, Convert.FromHexString(actualHash)))
        {
            return new UpdateArtifactAdmissionResult(
                UpdateOperationStatus.RejectedHash,
                actualHash);
        }

        try
        {
            using var archive = ZipFile.OpenRead(artifactPath);
            var candidates = archive.Entries
                .Where(IsPackagedPortableExecutable)
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
            {
                return new UpdateArtifactAdmissionResult(
                    UpdateOperationStatus.RejectedSignature,
                    actualHash);
            }

            var scratchRoot = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "EnviousWisprUpdateAdmission"));
            Directory.CreateDirectory(scratchRoot);
            var scratchFile = Path.Combine(scratchRoot, $"{Guid.NewGuid():N}.pe");
            var verified = 0;
            try
            {
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using (var destination = new FileStream(
                                     scratchFile,
                                     FileMode.Create,
                                     FileAccess.Write,
                                     FileShare.None,
                                     128 * 1024,
                                     FileOptions.Asynchronous))
                    await using (var source = candidate.Open())
                    {
                        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    }

                    if (!HasTrustedAuthenticodeSignature(scratchFile))
                    {
                        return new UpdateArtifactAdmissionResult(
                            UpdateOperationStatus.RejectedSignature,
                            actualHash,
                            verified);
                    }

                    var signerSubject = ReadSignerSubject(scratchFile);
                    if (signerSubject is null ||
                        !signerSubject.Contains(
                            admission.RequiredPublisherSubject,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return new UpdateArtifactAdmissionResult(
                            UpdateOperationStatus.RejectedPublisher,
                            actualHash,
                            verified);
                    }

                    verified++;
                }
            }
            finally
            {
                TryDeleteScratchFile(scratchRoot, scratchFile);
            }

            return new UpdateArtifactAdmissionResult(
                UpdateOperationStatus.DownloadedAndVerified,
                actualHash,
                verified);
        }
        catch (InvalidDataException)
        {
            return new UpdateArtifactAdmissionResult(UpdateOperationStatus.RejectedSignature, actualHash);
        }
        catch (IOException)
        {
            return new UpdateArtifactAdmissionResult(UpdateOperationStatus.Failed, actualHash);
        }
        catch (UnauthorizedAccessException)
        {
            return new UpdateArtifactAdmissionResult(UpdateOperationStatus.Failed, actualHash);
        }
        catch (SecurityException)
        {
            return new UpdateArtifactAdmissionResult(UpdateOperationStatus.Failed, actualHash);
        }
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Expected SHA-256 must contain exactly 64 hexadecimal characters.", nameof(value));
        }

        return normalized;
    }

    private static bool IsPackagedPortableExecutable(ZipArchiveEntry entry) =>
        entry.FullName.StartsWith("lib/app/", StringComparison.OrdinalIgnoreCase) &&
        (entry.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
         entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

    private static bool HasTrustedAuthenticodeSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData(fileInfoPointer);
            return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref trustData) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private static string? ReadSignerSubject(string path)
    {
#pragma warning disable SYSLIB0057 // The modern loader cannot read the signer embedded in a PE file.
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        return signer.Subject;
    }

    private static void TryDeleteScratchFile(string scratchRoot, string scratchFile)
    {
        var resolvedFile = Path.GetFullPath(scratchFile);
        var resolvedRoot = Path.GetFullPath(scratchRoot) + Path.DirectorySeparatorChar;
        if (!resolvedFile.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(resolvedFile);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (SecurityException)
        {
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        private readonly uint _structureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        private readonly string _filePath;
        private readonly IntPtr _fileHandle = IntPtr.Zero;
        private readonly IntPtr _knownSubject = IntPtr.Zero;

        public WinTrustFileInfo(string filePath) => _filePath = filePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        private uint _structureSize;
        private IntPtr _policyCallbackData;
        private IntPtr _sipClientData;
        private uint _uiChoice;
        private uint _revocationChecks;
        private uint _unionChoice;
        private IntPtr _fileInfo;
        private uint _stateAction;
        private IntPtr _stateData;
        private IntPtr _urlReference;
        private uint _providerFlags;
        private uint _uiContext;
        private IntPtr _signatureSettings;

        public WinTrustData(IntPtr fileInfo)
        {
            _structureSize = (uint)Marshal.SizeOf<WinTrustData>();
            _policyCallbackData = IntPtr.Zero;
            _sipClientData = IntPtr.Zero;
            _uiChoice = WinTrustNoUi;
            _revocationChecks = 0;
            _unionChoice = WinTrustFileChoice;
            _fileInfo = fileInfo;
            _stateAction = 0;
            _stateData = IntPtr.Zero;
            _urlReference = IntPtr.Zero;
            _providerFlags = WinTrustCacheOnlyUrlRetrieval;
            _uiContext = 0;
            _signatureSettings = IntPtr.Zero;
        }
    }
}
