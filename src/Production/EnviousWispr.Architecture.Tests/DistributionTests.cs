using System.IO.Compression;
using System.Security.Cryptography;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Services.Distribution;

namespace EnviousWispr.Architecture.Tests;

public sealed class DistributionTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        $"EnviousWisprDistributionTests-{Guid.NewGuid():N}");

    [Fact]
    public void ReleaseIdentitiesAreIsolatedAcrossEveryChannel()
    {
        var identities = Enum.GetValues<ReleaseChannel>()
            .Select(ReleaseIdentity.For)
            .ToArray();

        Assert.Equal(identities.Length, identities.Select(item => item.ChannelName).Distinct().Count());
        Assert.Equal(identities.Length, identities.Select(item => item.PackageId).Distinct().Count());
        Assert.Equal(identities.Length, identities.Select(item => item.DataDirectoryName).Distinct().Count());
        Assert.Equal(identities.Length, identities.Select(item => item.SingleInstanceKey).Distinct().Count());
    }

    [Theory]
    [InlineData("stable", ReleaseChannel.Stable)]
    [InlineData("win-x64-founder", ReleaseChannel.Founder)]
    [InlineData("BETA", ReleaseChannel.Beta)]
    public void ReleaseIdentityParserAcceptsOnlyBoundedChannels(
        string value,
        ReleaseChannel expected)
    {
        Assert.True(ReleaseIdentity.TryParse(value, out var identity));
        Assert.Equal(expected, identity.Channel);
    }

    [Fact]
    public void ReleaseIdentityParserFailsClosedToStable()
    {
        Assert.False(ReleaseIdentity.TryParse("nightly", out var identity));
        Assert.Equal(ReleaseIdentity.Stable, identity);
    }

    [Fact]
    public void UpdateEndpointAcceptsAndNormalizesHttpsOnly()
    {
        Assert.True(UpdateEndpointPolicy.TryNormalize(
            "https://updates.enviouslabs.com/windows/founder",
            allowLoopbackForUat: false,
            out var endpoint));

        Assert.Equal(
            "https://updates.enviouslabs.com/windows/founder/",
            endpoint!.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://updates.enviouslabs.com/windows")]
    [InlineData("https://user:secret@updates.enviouslabs.com/windows")]
    [InlineData("https://updates.enviouslabs.com/windows?channel=founder")]
    [InlineData("https://updates.enviouslabs.com/windows#founder")]
    public void UpdateEndpointRejectsInsecureOrMutableAuthority(string value)
    {
        Assert.False(UpdateEndpointPolicy.TryNormalize(
            value,
            allowLoopbackForUat: false,
            out var endpoint));
        Assert.Null(endpoint);
    }

    [Fact]
    public void LoopbackHttpRequiresExplicitUatGate()
    {
        const string value = "http://127.0.0.1:43192";

        Assert.False(UpdateEndpointPolicy.TryNormalize(
            value,
            allowLoopbackForUat: false,
            out _));
        Assert.True(UpdateEndpointPolicy.TryNormalize(
            value,
            allowLoopbackForUat: true,
            out var endpoint));
        Assert.Equal("http://127.0.0.1:43192/", endpoint!.AbsoluteUri);
    }

    [Fact]
    public async Task AdmissionRejectsWrongHashBeforeOpeningPackage()
    {
        Directory.CreateDirectory(_scratch);
        var path = Path.Combine(_scratch, "EnviousLabs.EnviousWispr-1.0.0-full.nupkg");
        await File.WriteAllTextAsync(path, "not a package");
        var validator = new WindowsUpdateArtifactValidator();

        var result = await validator.ValidateAsync(new UpdateArtifactAdmission(
            path,
            new string('0', 64),
            "Envious Labs",
            ReleaseIdentity.Stable));

        Assert.Equal(UpdateOperationStatus.RejectedHash, result.Status);
    }

    [Fact]
    public async Task AdmissionRejectsUnsignedPortableExecutable()
    {
        var path = await CreatePackageAsync(
            ReleaseIdentity.Stable.PackageId,
            typeof(DistributionTests).Assembly.Location);
        var validator = new WindowsUpdateArtifactValidator();

        var result = await validator.ValidateAsync(new UpdateArtifactAdmission(
            path,
            await Sha256Async(path),
            "Envious Labs",
            ReleaseIdentity.Stable));

        Assert.Equal(UpdateOperationStatus.RejectedSignature, result.Status);
    }

    [Fact]
    public async Task AdmissionAcceptsTrustedPortableExecutableFromRequiredPublisher()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var signedSystemBinary = Path.Combine(windowsDirectory, "System32", "kernel32.dll");
        var path = await CreatePackageAsync(ReleaseIdentity.Stable.PackageId, signedSystemBinary);
        var validator = new WindowsUpdateArtifactValidator();

        var result = await validator.ValidateAsync(new UpdateArtifactAdmission(
            path,
            await Sha256Async(path),
            "Microsoft Windows",
            ReleaseIdentity.Stable));

        Assert.Equal(UpdateOperationStatus.DownloadedAndVerified, result.Status);
        Assert.Equal(1, result.VerifiedPortableExecutableCount);
    }

    [Fact]
    public async Task AdmissionRejectsTrustedPortableExecutableFromAnotherPublisher()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var signedSystemBinary = Path.Combine(windowsDirectory, "System32", "kernel32.dll");
        var path = await CreatePackageAsync(ReleaseIdentity.Stable.PackageId, signedSystemBinary);
        var validator = new WindowsUpdateArtifactValidator();

        var result = await validator.ValidateAsync(new UpdateArtifactAdmission(
            path,
            await Sha256Async(path),
            "Envious Labs",
            ReleaseIdentity.Stable));

        Assert.Equal(UpdateOperationStatus.RejectedPublisher, result.Status);
    }

    [Fact]
    public async Task AdmissionRejectsArtifactFromAnotherChannelIdentity()
    {
        var path = await CreatePackageAsync(
            ReleaseIdentity.For(ReleaseChannel.Beta).PackageId,
            typeof(DistributionTests).Assembly.Location);
        var validator = new WindowsUpdateArtifactValidator();

        var result = await validator.ValidateAsync(new UpdateArtifactAdmission(
            path,
            await Sha256Async(path),
            "Envious Labs",
            ReleaseIdentity.Stable));

        Assert.Equal(UpdateOperationStatus.RejectedChannel, result.Status);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_scratch))
        {
            return;
        }

        var resolved = Path.GetFullPath(_scratch);
        var expectedRoot = Path.GetFullPath(Path.GetTempPath()) + Path.DirectorySeparatorChar;
        if (resolved.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }

    private async Task<string> CreatePackageAsync(string packageId, string portableExecutable)
    {
        Directory.CreateDirectory(_scratch);
        var path = Path.Combine(_scratch, $"{packageId}-1.0.0-full.nupkg");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        var entry = archive.CreateEntry("lib/app/test.dll", CompressionLevel.NoCompression);
        await using var entryStream = entry.Open();
        await using var source = File.OpenRead(portableExecutable);
        await source.CopyToAsync(entryStream);
        return path;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
