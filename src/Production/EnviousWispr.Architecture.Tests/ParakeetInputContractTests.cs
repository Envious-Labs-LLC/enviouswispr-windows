using System.Text.Json;
using EnviousWispr.ASR;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.ModelDelivery;

namespace EnviousWispr.Architecture.Tests;

public sealed class ParakeetInputContractTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(160)]
    [InlineData(256)]
    public void ReflectionPaddingRefusesShortNonemptyInputBeforeAdmission(int samples)
    {
        var failure = Assert.Throws<TranscriptionEngineException>(
            () => ParakeetTranscriptionEngine.ValidateInputLength(samples));

        Assert.Equal(AppErrorCode.TranscriptionInputTooShort, failure.Error.Code);
        Assert.Equal(AppErrorStage.FinalAsr, failure.Error.Stage);
        Assert.False(failure.Error.CanRetry);
        Assert.Equal(AppFailureCategory.AsrUnavailable, AppFailureCategories.For(failure.Error));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    [InlineData(512)]
    public void TheLengthGuardAllowsEmptyInputAndThePreprocessorBoundary(int samples) =>
        ParakeetTranscriptionEngine.ValidateInputLength(samples);

    [Fact]
    public void CancellationWinsOverTheShortInputRefusal()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = Assert.ThrowsAny<OperationCanceledException>(
            () => ParakeetTranscriptionEngine.ValidateInputLength(160, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Fact]
    public void APreprocessorArtifactChangeRequiresReviewingTheLengthContract()
    {
        using var manifest = JsonDocument.Parse(
            Assert.IsType<byte[]>(BundledModelManifests.TryRead(ParakeetTranscriptionEngine.ModelId)));
        var preprocessor = Assert.Single(manifest.RootElement.GetProperty("files").EnumerateArray(),
            file => file.GetProperty("relativePath").GetString() == "nemo128.onnx");

        Assert.Equal("a9fde1486ebfcc08f328d75ad4610c67835fea58c73ba57e3209a6f6cf019e9f",
            preprocessor.GetProperty("sha256").GetString());
        Assert.Equal(257, ParakeetTranscriptionEngine.MinimumPreprocessorInputSamples);
    }

    [Fact]
    public void TheWorkerResponsePreservesTheDistinctNonretryableRefusal()
    {
        var requestId = Guid.NewGuid();
        var response = new RuntimeWorkerResponse(1, requestId, "failed", Error: new AppError(
            AppErrorCode.TranscriptionInputTooShort, AppErrorStage.FinalAsr, CanRetry: false));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var actual = JsonSerializer.Deserialize<RuntimeWorkerResponse>(
            JsonSerializer.Serialize(response, options), options);

        Assert.NotNull(actual);
        Assert.Equal(requestId, actual.RequestId);
        Assert.Equal(response.Error, actual.Error);
        Assert.Equal(24, (int)AppErrorCode.TranscriptionFailed);
        Assert.True((int)AppErrorCode.TranscriptionInputTooShort > (int)AppErrorCode.DeliveryFaulted);
    }
}
