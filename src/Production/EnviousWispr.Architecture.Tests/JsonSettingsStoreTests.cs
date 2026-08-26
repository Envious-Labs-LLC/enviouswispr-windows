using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public async Task SaveThenLoadRestoresVersionedSettings()
    {
        var directory = CreateTestDirectory();
        try
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            var expected = AppSettings.Default with { LaunchCount = 7, HasCompletedOnboarding = true };

            await store.SaveAsync(expected);
            var result = await store.LoadAsync();

            Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
            Assert.Equal(expected, result.Settings);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadInvalidJsonReturnsSafeDefaultsWithoutThrowing()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, "not-json");
            var store = new JsonSettingsStore(path);

            var result = await store.LoadAsync();

            Assert.Equal(SettingsLoadStatus.Invalid, result.Status);
            Assert.Equal(AppSettings.Default, result.Settings);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task LoadInvalidLaunchCountReturnsSafeDefaultsWithoutThrowing(int launchCount)
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(
                path,
                $$"""
                {
                  "schemaVersion": 1,
                  "launchCount": {{launchCount}},
                  "hasCompletedOnboarding": false
                }
                """);
            var store = new JsonSettingsStore(path);

            var result = await store.LoadAsync();

            Assert.Equal(SettingsLoadStatus.Invalid, result.Status);
            Assert.Equal(AppSettings.Default, result.Settings);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "EnviousWispr.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
