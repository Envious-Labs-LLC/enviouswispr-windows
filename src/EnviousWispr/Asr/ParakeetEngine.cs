using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EnviousWispr.Asr;

public sealed record AsrResult(string Text, long ElapsedMs);

/// Parakeet TDT 0.6B v3 on ONNX Runtime — faithful port of the measured
/// onnx-asr reference pipeline (spike S1): nemo128 mel preprocessor →
/// int8 QDQ encoder → per-frame transducer (TDT) greedy decode.
///
/// Thread config is a production requirement, not a tuning nicety:
/// S1 measured 7-10x latency difference between ORT default (all logical
/// processors) and intra_op 6-8 on hybrid-core chips (i9-14900KF).
public sealed class ParakeetEngine : IDisposable
{
    private readonly InferenceSession _preprocessor;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly Vocab _vocab;
    private readonly int _maxTokensPerStep;
    private readonly int _encoderDim;
    private readonly int _stateDim;       // last dim of the prednet GRU state (640)
    private readonly int _stateLeading;   // leading dim of the state tensor (2)

    // Reused across decode-loop calls to keep per-frame allocation cheap.
    // targets/target_length are INT32 per the decoder graph metadata (the
    // preprocessor/encoder graphs take INT64 — verified per-node by ORT).
    private float[] _encFrame = [];
    private int[] _targets = [0];
    private readonly int[] _targetLength = [1];
    private string[] _decoderOutputs = [];

    public string ModelDir { get; }

    public ParakeetEngine(string modelDir, int intraOpThreads, int interOpThreads,
        int maxTokensPerStep, bool useCuda)
    {
        ModelDir = modelDir;
        _maxTokensPerStep = maxTokensPerStep;

        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = intraOpThreads,
            InterOpNumThreads = interOpThreads,
        };
        // 1.22 API: provider is an option-method, not a ctor array. CUDA EP
        // falls back to CPU per-node if it cannot host an op.
        if (useCuda) so.AppendExecutionProvider_CUDA(0);

        _preprocessor = new InferenceSession(Path.Combine(modelDir, "nemo128.onnx"), so);
        _encoder = new InferenceSession(Path.Combine(modelDir, "encoder-model.int8.onnx"), so);
        _decoder = new InferenceSession(Path.Combine(modelDir, "decoder_joint-model.int8.onnx"), so);
        _vocab = Vocab.Load(Path.Combine(modelDir, "vocab.txt"));

        // Probe decoder geometry once from the graph metadata (MEASURED):
        //   encoder_outputs [1, 1024, 1] f32 ; targets [1,1] i32 ; target_length [1] i32
        //   input_states_1/2 [2, 1, 640] f32 ; outputs [1,1,1,8198] f32 (8193 + 5 step)
        _decoderOutputs = new[] { "outputs", "output_states_1", "output_states_2" };
        var inputs = _decoder.InputMetadata;
        var s1 = inputs["input_states_1"];
        _encoderDim = (int)inputs["encoder_outputs"].Dimensions[1];
        _stateDim = (int)s1.Dimensions[2];
        _stateLeading = (int)s1.Dimensions[0];
        // NOTE: the decoder "outputs" width is dynamic (-1 in the graph).
        // The decode loop reads the real width from each result tensor and
        // splits it at vocab size (logits = [0..V), step scores = [V..W)).
    }

    /// Transcribes 16 kHz mono float samples (exactly the capture spec).
    public AsrResult Recognize(ReadOnlySpan<float> samples16k)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Mel features (nemo128 preprocessor ONNX session).
        var wave = samples16k.ToArray();
        var waveLens = new long[] { wave.Length };
        var pre = _preprocessor.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("waveforms", new DenseTensor<float>(wave.AsMemory(), new int[] { 1, wave.Length })),
            NamedOnnxValue.CreateFromTensor("waveforms_lens", new DenseTensor<long>(waveLens.AsMemory(), new int[] { 1 })),
        });
        var features = pre[0].AsEnumerable<float>().ToArray();   // [128, T]
        var featureLens = pre[1].AsEnumerable<long>().Single();
        var tFeats = (int)featureLens;

        // 2. Encoder (single call; the dominant stage — S1: 96% of CPU time).
        var enc = _encoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("audio_signal", new DenseTensor<float>(features.AsMemory(), new int[] { 1, 128, tFeats })),
            NamedOnnxValue.CreateFromTensor("length", new DenseTensor<long>(new long[] { featureLens }, new int[] { 1 })),
        });
        // Encoder graph layout is [1, dim, T]; the decode loop wants [T, dim]
        // (the reference transposes (0, 2, 1)). Transpose explicitly.
        var encRaw = enc[0].AsEnumerable<float>().ToArray();     // [1, dim, T]
        var tFull = encRaw.Length / _encoderDim;
        var encOut = new float[tFull * _encoderDim];
        for (var t = 0; t < tFull; t++)
            for (var d = 0; d < _encoderDim; d++)
                encOut[t * _encoderDim + d] = encRaw[d * tFull + t];
        var encLens = enc[1].AsEnumerable<long>().Single();
        var tEnc = (int)encLens;

        // 3. TDT greedy decode — one decoder_joint call per encoder frame,
        //    step logic ported line-for-line from onnx-asr.
        var tokens = Decode(encOut, tEnc);

        // 4. Token → text (subword join + space cleanup per the reference).
        var text = TokensToText(tokens);
        sw.Stop();
        return new AsrResult(text, sw.ElapsedMilliseconds);
    }

    private List<int> Decode(float[] encOut, int tEnc)
    {
        var tokens = new List<int>();
        var state1 = new float[_stateLeading * 1 * _stateDim];
        var state2 = new float[_stateLeading * 1 * _stateDim];
        _encFrame = new float[1 * _encoderDim * 1];

        var t = 0;
        var emitted = 0;
        while (t < tEnc)
        {
            // encoder_outputs: [1, dim, 1] — frame t as a column.
            for (var d = 0; d < _encoderDim; d++)
                _encFrame[d] = encOut[t * _encoderDim + d];
            _targets[0] = tokens.Count > 0 ? tokens[^1] : _vocab.BlankIdx;

            var res = _decoder.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("encoder_outputs", new DenseTensor<float>(_encFrame.AsMemory(), new int[] { 1, _encoderDim, 1 })),
                NamedOnnxValue.CreateFromTensor("targets", new DenseTensor<int>(_targets.AsMemory(), new int[] { 1, 1 })),
                NamedOnnxValue.CreateFromTensor("target_length", new DenseTensor<int>(_targetLength.AsMemory(), new int[] { 1 })),

                NamedOnnxValue.CreateFromTensor("input_states_1", new DenseTensor<float>(state1.AsMemory(), new int[] { _stateLeading, 1, _stateDim })),
                NamedOnnxValue.CreateFromTensor("input_states_2", new DenseTensor<float>(state2.AsMemory(), new int[] { _stateLeading, 1, _stateDim })),
            },
            // Explicit output list — Run(inputs) alone would also return
            // prednet_lengths (int32) at index 1.
            _decoderOutputs);

            var output = res[0].AsEnumerable<float>().ToArray(); // [1, 1, V+K]
            int token = -1;
            float best = float.NegativeInfinity;
            for (var i = 0; i < _vocab.VocabSize; i++)
            {
                if (output[i] > best) { best = output[i]; token = i; }
            }
            int step = 0;
            best = float.NegativeInfinity;

            for (var i = _vocab.VocabSize; i < output.Length; i++)
            {
                if (output[i] > best) { best = output[i]; step = i - _vocab.VocabSize; }
            }

            if (token != _vocab.BlankIdx)
            {
                Array.Copy(res[1].AsEnumerable<float>().ToArray(), state1, state1.Length);
                Array.Copy(res[2].AsEnumerable<float>().ToArray(), state2, state2.Length);
                tokens.Add(token);
                emitted++;
            }

            if (step > 0)
            {
                t += step;
                emitted = 0;
            }
            else if (token == _vocab.BlankIdx || emitted >= _maxTokensPerStep)
            {
                t++;
                emitted = 0;
            }
        }
        return tokens;
    }

    private static readonly System.Text.RegularExpressions.Regex SpacePattern =
        new(@"\A\s|\s\B|(\s)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private string TokensToText(List<int> tokens)
    {
        if (tokens.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var tok in tokens) sb.Append(_vocab.Tokens[tok]);
        // Reference decode cleanup: drop leading space, drop spaces before
        // punctuation (non-word chars), collapse runs (each space before a
        // word boundary survives, the ones before non-word chars don't).
        return SpacePattern.Replace(sb.ToString(), m => m.Groups[1].Success ? " " : "");
    }

    public void Dispose()
    {
        _preprocessor.Dispose();
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
