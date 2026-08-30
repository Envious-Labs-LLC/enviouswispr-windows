namespace EnviousWispr.Core.Distribution;

public interface IUpdateArtifactValidator
{
    Task<UpdateArtifactAdmissionResult> ValidateAsync(
        UpdateArtifactAdmission admission,
        CancellationToken cancellationToken = default);
}
