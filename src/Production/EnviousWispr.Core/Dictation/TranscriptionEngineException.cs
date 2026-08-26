using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Dictation;

public sealed class TranscriptionEngineException : Exception
{
    public TranscriptionEngineException(AppError error, Exception? innerException = null)
        : base("The local transcription engine could not complete the request.", innerException)
    {
        Error = error;
    }

    public AppError Error { get; }
}
