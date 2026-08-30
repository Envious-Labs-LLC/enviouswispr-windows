namespace EnviousWispr.Core.Settings;

public interface ISettingsStore
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task<SettingsResetResult> ResetAsync(
        AppSettings replacement,
        CancellationToken cancellationToken = default);
}
