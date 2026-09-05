namespace EnviousWispr.ModelDelivery;

public sealed class ModelManifestClient
{
    private readonly HttpClient _httpClient;
    private readonly ModelManifestVerifier _verifier;

    public ModelManifestClient(HttpClient httpClient, ModelManifestVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(verifier);
        _httpClient = httpClient;
        _verifier = verifier;
    }

    public async Task<ManifestVerificationResult> FetchAndVerifyAsync(
        Uri manifestUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        if (!manifestUri.IsAbsoluteUri ||
            (manifestUri.Scheme != Uri.UriSchemeHttps &&
                !(manifestUri.Scheme == Uri.UriSchemeHttp && manifestUri.IsLoopback)))
        {
            return new(ManifestVerificationStatus.InvalidEnvelope);
        }

        try
        {
            using var response = await _httpClient.GetAsync(
                manifestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new(ManifestVerificationStatus.Unreachable);
            }

            if (response.Content.Headers.ContentLength > ModelManifestVerifier.MaximumEnvelopeBytes)
            {
                return new(ManifestVerificationStatus.InvalidEnvelope);
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var destination = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (destination.Length + read > ModelManifestVerifier.MaximumEnvelopeBytes)
                {
                    return new(ManifestVerificationStatus.InvalidEnvelope);
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            return _verifier.Verify(destination.GetBuffer().AsSpan(0, checked((int)destination.Length)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ManifestVerificationStatus.Unreachable);
        }
        catch (HttpRequestException)
        {
            return new(ManifestVerificationStatus.Unreachable);
        }
    }
}
