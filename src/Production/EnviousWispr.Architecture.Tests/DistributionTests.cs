using System.Xml.Linq;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Services.Distribution;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace EnviousWispr.Architecture.Tests;

public sealed class DistributionTests
{
    [Fact]
    public void ReleaseIdentitiesAreIsolatedAcrossEveryChannel()
    {
        var identities = Enum.GetValues<ReleaseChannel>()
            .Select(ReleaseIdentity.For)
            .ToArray();

        Assert.Equal(identities.Length, identities.Select(item => item.ChannelName).Distinct().Count());
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

    /// <summary>
    /// EVERY STATE THE STORE CAN END AN INSTALL ON, WRITTEN OUT. Only Completed is an install and only
    /// Canceled is the person saying no; the rest left this copy unchanged. The table is literal and must
    /// cover the Store's whole enum, so a state a later SDK adds fails here until someone decides what it
    /// means, rather than falling silently into the default.
    /// </summary>
    [Fact]
    public void EveryStoreInstallStateMapsToTheStatusThePersonSees()
    {
        var expected = new Dictionary<StorePackageUpdateState, UpdateOperationStatus>
        {
            [StorePackageUpdateState.Pending] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.Downloading] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.Deploying] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.Completed] = UpdateOperationStatus.Installing,
            [StorePackageUpdateState.Canceled] = UpdateOperationStatus.Cancelled,
            [StorePackageUpdateState.OtherError] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.ErrorLowBattery] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.ErrorWiFiRecommended] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.ErrorWiFiRequired] = UpdateOperationStatus.Failed,
        };

        Assert.Equal(
            Enum.GetValues<StorePackageUpdateState>().Order(),
            expected.Keys.Order());
        foreach (var (state, status) in expected)
        {
            Assert.Equal(status, StoreUpdateOutcome.FromInstall(state));
        }
    }

    [Fact]
    public void ACheckWithNothingOfferedIsNoUpdateAndNamesTheInstalledVersion()
    {
        var result = StoreUpdateOutcome.FromCheck([], "0.19.0.0");

        Assert.Equal(new UpdateOperationResult(UpdateOperationStatus.NoUpdate, "0.19.0.0"), result);
        Assert.False(result.CanApply);
    }

    /// <summary>
    /// THE NEWEST BY NUMBER, WHATEVER THE ORDER. Each pair differs in one part only, and the smaller one
    /// sorts later as text (9 after 10), so a first-item or string comparison picks the wrong version.
    /// </summary>
    [Theory]
    [InlineData(new ushort[] { 1, 9, 0, 0 }, new ushort[] { 1, 10, 0, 0 }, "1.10.0.0")]
    [InlineData(new ushort[] { 1, 2, 9, 0 }, new ushort[] { 1, 2, 10, 0 }, "1.2.10.0")]
    [InlineData(new ushort[] { 1, 2, 3, 9 }, new ushort[] { 1, 2, 3, 10 }, "1.2.3.10")]
    [InlineData(new ushort[] { 9, 65535, 65535, 65535 }, new ushort[] { 10, 0, 0, 0 }, "10.0.0.0")]
    public void ACheckOffersTheNewestVersionInEitherOrder(ushort[] older, ushort[] newer, string expected)
    {
        var olderVersion = new PackageVersion(older[0], older[1], older[2], older[3]);
        var newerVersion = new PackageVersion(newer[0], newer[1], newer[2], newer[3]);

        foreach (var offered in new[] { new[] { olderVersion, newerVersion }, new[] { newerVersion, olderVersion } })
        {
            var result = StoreUpdateOutcome.FromCheck(offered, "0.19.0.0");

            Assert.Equal(new UpdateOperationResult(UpdateOperationStatus.UpdateAvailable, expected), result);
            Assert.True(result.CanApply);
        }
    }

    /// <summary>Only an update the Store has offered can be installed; every other answer leaves Install off.</summary>
    [Fact]
    public void OnlyAnAvailableUpdateCanBeApplied()
    {
        var applicable = Enum.GetValues<UpdateOperationStatus>()
            .Where(status => new UpdateOperationResult(status, "1.0.0.0").CanApply)
            .ToArray();

        Assert.Equal(new[] { UpdateOperationStatus.UpdateAvailable }, applicable);
    }

    [Fact]
    public void EveryStartupTaskStateMapsToTheStatusThePersonSees()
    {
        var expected = new Dictionary<StartupTaskState, StartupRegistrationStatus>
        {
            [StartupTaskState.Disabled] = StartupRegistrationStatus.Disabled,
            [StartupTaskState.DisabledByUser] = StartupRegistrationStatus.DisabledByUser,
            [StartupTaskState.Enabled] = StartupRegistrationStatus.Enabled,
            [StartupTaskState.DisabledByPolicy] = StartupRegistrationStatus.DisabledByPolicy,
            [StartupTaskState.EnabledByPolicy] = StartupRegistrationStatus.EnabledByPolicy,
        };

        Assert.Equal(
            Enum.GetValues<StartupTaskState>().Order(),
            expected.Keys.Order());
        foreach (var (state, status) in expected)
        {
            Assert.Equal(status, WindowsStartupRegistration.FromTaskState(state));
        }
    }

    [Fact]
    public async Task PackagedStartupTaskQueriesEnviousWisprStartupAndTogglesWithoutTouchingRegistry()
    {
        var task = new FakePackagedStartupTask(StartupTaskState.Disabled);
        var registry = new FakeStartupRegistryStore();
        string? requestedTaskId = null;
        var registration = new WindowsStartupRegistration(
            isPackaged: () => true,
            getStartupTask: (taskId, _) =>
            {
                requestedTaskId = taskId;
                return Task.FromResult<IPackagedStartupTask>(task);
            },
            registry,
            executablePath: () => @"C:\Program Files\WindowsApps\EnviousWispr\EnviousWispr.App.exe");

        var initial = await registration.GetStatusAsync();
        Assert.Equal(WindowsStartupRegistration.StartupTaskId, requestedTaskId);
        Assert.Equal(new StartupRegistrationResult(StartupRegistrationStatus.Disabled, IsPackaged: true), initial);
        Assert.False(initial.IsEnabled);
        Assert.True(initial.CanToggle);

        var enabled = await registration.SetEnabledAsync(true);
        Assert.Equal(1, task.EnableRequests);
        Assert.Equal(0, task.DisableCalls);
        Assert.Equal(new StartupRegistrationResult(StartupRegistrationStatus.Enabled, IsPackaged: true), enabled);
        Assert.True(enabled.IsEnabled);

        var disabled = await registration.SetEnabledAsync(false);
        Assert.Equal(1, task.EnableRequests);
        Assert.Equal(1, task.DisableCalls);
        Assert.Equal(new StartupRegistrationResult(StartupRegistrationStatus.Disabled, IsPackaged: true), disabled);
        Assert.False(disabled.IsEnabled);
        Assert.Equal(0, registry.Reads);
        Assert.Equal(0, registry.Writes);
        Assert.Equal(0, registry.Deletes);
    }

    [Fact]
    public async Task PackagedStartupTaskHonorsDisabledByUserAndNeverCallsRequestEnableOrRegistry()
    {
        var task = new FakePackagedStartupTask(StartupTaskState.DisabledByUser);
        var registry = new FakeStartupRegistryStore();
        var registration = new WindowsStartupRegistration(
            isPackaged: () => true,
            getStartupTask: (_, _) => Task.FromResult<IPackagedStartupTask>(task),
            registry,
            executablePath: () => @"C:\Program Files\WindowsApps\EnviousWispr\EnviousWispr.App.exe");

        var status = await registration.GetStatusAsync();
        Assert.Equal(StartupRegistrationStatus.DisabledByUser, status.Status);
        Assert.False(status.IsEnabled);
        Assert.False(status.CanToggle);
        Assert.True(status.NeedsWindowsStartupSettings);

        var attemptedEnable = await registration.SetEnabledAsync(true);
        Assert.Equal(StartupRegistrationStatus.DisabledByUser, attemptedEnable.Status);
        Assert.Equal(0, task.EnableRequests);
        Assert.Equal(0, task.DisableCalls);
        Assert.Equal(0, registry.Writes);
        Assert.Equal(0, registry.Deletes);
    }

    [Theory]
    [InlineData(StartupTaskState.DisabledByPolicy, StartupRegistrationStatus.DisabledByPolicy, false)]
    [InlineData(StartupTaskState.EnabledByPolicy, StartupRegistrationStatus.EnabledByPolicy, true)]
    public async Task PackagedStartupTaskHonorsPolicyStatesWithoutMutating(
        StartupTaskState state,
        StartupRegistrationStatus expectedStatus,
        bool expectedEnabled)
    {
        var task = new FakePackagedStartupTask(state);
        var registry = new FakeStartupRegistryStore();
        var registration = new WindowsStartupRegistration(
            isPackaged: () => true,
            getStartupTask: (_, _) => Task.FromResult<IPackagedStartupTask>(task),
            registry,
            executablePath: () => @"C:\Program Files\WindowsApps\EnviousWispr\EnviousWispr.App.exe");

        var toggled = await registration.SetEnabledAsync(!expectedEnabled);
        Assert.Equal(expectedStatus, toggled.Status);
        Assert.Equal(expectedEnabled, toggled.IsEnabled);
        Assert.False(toggled.CanToggle);
        Assert.Equal(0, task.EnableRequests);
        Assert.Equal(0, task.DisableCalls);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public async Task UnpackagedStartupRegistrationFallsBackToPerUserRunRegistryCleanly()
    {
        var registry = new FakeStartupRegistryStore();
        var packagedCalls = 0;
        var registration = new WindowsStartupRegistration(
            isPackaged: () => false,
            getStartupTask: (_, _) =>
            {
                packagedCalls++;
                throw new InvalidOperationException("Unpackaged startup must not call StartupTask.");
            },
            registry,
            executablePath: () => @"C:\TestProfile\AppData\Local\EnviousWispr\EnviousWispr.App.exe");

        var initial = await registration.GetStatusAsync();
        Assert.Equal(new StartupRegistrationResult(StartupRegistrationStatus.Disabled, IsPackaged: false), initial);

        var enabled = await registration.SetEnabledAsync(true);
        Assert.Equal(new StartupRegistrationResult(StartupRegistrationStatus.Enabled, IsPackaged: false), enabled);
        Assert.Equal("\"C:\\TestProfile\\AppData\\Local\\EnviousWispr\\EnviousWispr.App.exe\"", registry.Command);

        var disabled = await registration.SetEnabledAsync(false);
        Assert.Equal(new StartupRegistrationResult(StartupRegistrationStatus.Disabled, IsPackaged: false), disabled);
        Assert.Null(registry.Command);
        Assert.Equal(0, packagedCalls);
    }

    [Fact]
    public async Task UnpackagedStartupRegistrationTreatsRunCommandForAnotherExecutableAsDisabled()
    {
        const string legacyWpfCommand = "\"C:\\TestProfile\\AppData\\Local\\EnviousWispr-WpfProof\\EnviousWispr.exe\"";
        const string winUiExecutable = @"C:\TestProfile\AppData\Local\EnviousWispr\EnviousWispr.App.exe";

        var registry = new FakeStartupRegistryStore();
        registry.WriteCommand(legacyWpfCommand);
        var registration = new WindowsStartupRegistration(
            isPackaged: () => false,
            getStartupTask: (_, _) => throw new InvalidOperationException("Unpackaged startup must not call StartupTask."),
            registry,
            executablePath: () => winUiExecutable);

        // A Run entry pointing to a different executable (such as the preserved WPF proof) must not report this copy as enabled.
        var initial = await registration.GetStatusAsync();
        Assert.Equal(new StartupRegistrationResult(StartupRegistrationStatus.Disabled, IsPackaged: false), initial);

        // Turning startup off for this copy when it is not the registered executable leaves the other executable's entry intact.
        var disabled = await registration.SetEnabledAsync(false);
        Assert.Equal(new StartupRegistrationResult(StartupRegistrationStatus.Disabled, IsPackaged: false), disabled);
        Assert.Equal(legacyWpfCommand, registry.Command);
        Assert.Equal(0, registry.Deletes);

        // Turning startup on for this copy claims the Run entry with this executable's path.
        var enabled = await registration.SetEnabledAsync(true);
        Assert.Equal(new StartupRegistrationResult(StartupRegistrationStatus.Enabled, IsPackaged: false), enabled);
        Assert.Equal($"\"{winUiExecutable}\"", registry.Command);
    }

    [Fact]
    public void PackageManifestDeclaresEnviousWisprStartupTaskDisabledByDefault()
    {
        var manifestPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Production",
            "EnviousWispr.App",
            "Package.appxmanifest");
        var document = XDocument.Load(manifestPath);
        XNamespace desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";

        var startupTask = document.Descendants(desktop + "StartupTask").Single();
        Assert.Equal(WindowsStartupRegistration.StartupTaskId, (string?)startupTask.Attribute("TaskId"));
        Assert.Equal("false", (string?)startupTask.Attribute("Enabled"));
    }

    [Fact]
    public void EgOneRuntimeLocatorEnforcesBundledServerForStoreInstallAndPreservesUnpackagedFallback()
    {
        const string baseDir = @"C:\Program Files\WindowsApps\EnviousLabsLLC.EnviousWispr_0.19.0.0_x64";
        const string dataDir = @"C:\TestProfile\AppData\Local\Envious Labs\EnviousWispr";
        var bundled = Path.Combine(baseDir, "runtime", "llama-server.exe");
        var provisioned = Path.Combine(dataDir, "runtime", "llama.cpp", "llama-server.exe");

        // Store-installed (packaged) builds must ALWAYS resolve to the signed package's bundled
        // runtime\llama-server.exe even if a binary exists in the writable data directory or an
        // environment override is present (policy 10.2.2).
        const string uatOverride = @"C:\UAT\llama-server.exe";
        var packagedResolved = EgOneRuntimeLocator.ResolveServerExecutable(
            baseDir,
            dataDir,
            isStoreInstalled: true,
            environmentOverride: uatOverride,
            fileExists: path => path == provisioned);
        Assert.Equal(bundled, packagedResolved);

        // Unpackaged developer builds prefer the founder-provisioned data-directory runtime first
        // even when a bundled copy is also present, falling back to bundled only when the data-directory
        // copy does not exist (.claude/knowledge/distribution.md:45-46).
        var unpackagedProvisionedFirst = EgOneRuntimeLocator.ResolveServerExecutable(
            baseDir,
            dataDir,
            isStoreInstalled: false,
            environmentOverride: null,
            fileExists: _ => true);
        Assert.Equal(provisioned, unpackagedProvisionedFirst);

        var unpackagedBundledFallback = EgOneRuntimeLocator.ResolveServerExecutable(
            baseDir,
            dataDir,
            isStoreInstalled: false,
            environmentOverride: null,
            fileExists: _ => false);
        Assert.Equal(bundled, unpackagedBundledFallback);

        // Explicit environment override applies only to unpackaged UAT harnesses.
        var overridden = EgOneRuntimeLocator.ResolveServerExecutable(
            baseDir,
            dataDir,
            isStoreInstalled: false,
            environmentOverride: uatOverride,
            fileExists: _ => true);
        Assert.Equal(uatOverride, overridden);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class FakePackagedStartupTask(StartupTaskState initialState) : IPackagedStartupTask
    {
        public StartupTaskState State { get; private set; } = initialState;

        public StartupTaskState NextEnableState { get; init; } = StartupTaskState.Enabled;

        public int EnableRequests { get; private set; }

        public int DisableCalls { get; private set; }

        public Task<StartupTaskState> RequestEnableAsync(CancellationToken cancellationToken = default)
        {
            EnableRequests++;
            State = NextEnableState;
            return Task.FromResult(State);
        }

        public void Disable()
        {
            DisableCalls++;
            State = StartupTaskState.Disabled;
        }
    }

    private sealed class FakeStartupRegistryStore : IStartupRegistryStore
    {
        public string? Command { get; private set; }

        public int Reads { get; private set; }

        public int Writes { get; private set; }

        public int Deletes { get; private set; }

        public string? ReadCommand()
        {
            Reads++;
            return Command;
        }

        public void WriteCommand(string command)
        {
            Writes++;
            Command = command;
        }

        public void DeleteCommand()
        {
            Deletes++;
            Command = null;
        }
    }
}
