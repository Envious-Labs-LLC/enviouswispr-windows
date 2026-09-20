using System.Text.Json;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

public sealed class DeterministicTextPipelineTests
{
    /// <summary>
    /// How long a test waits for a barrier the executor must cross. A guard against a hang, not a
    /// measurement: the deadlines under test are the steps' own (250 ms and below), and the guard
    /// only decides how quickly a broken executor fails. Five seconds was crossed twice in one day
    /// by a starved hosted runner that had not yet scheduled the blocking step (#165).
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task SharedParityFixtureMatchesPinnedMacBehaviorAndDocumentedDifferences()
    {
        var cases = await LoadParityCasesAsync();
        var pipeline = new DeterministicTextPipeline();

        Assert.True(cases.Count >= 35, "The deterministic parity fixture was unexpectedly reduced.");
        Assert.Contains(cases, item => item.Category == "international-filler");
        Assert.Contains(cases, item => item.Category == "international-itn");
        Assert.All(cases.Where(item => item.Difference is not null), item =>
            Assert.False(string.IsNullOrWhiteSpace(item.Difference)));

        var mismatches = new List<string>();
        foreach (var item in cases)
        {
            var result = await pipeline.ProcessAsync(CreateRequest(item));
            if (!string.Equals(item.Expected, result.Output.Text, StringComparison.Ordinal))
            {
                mismatches.Add($"{item.Name}: expected '{item.Expected}', got '{result.Output.Text}'");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Theory]
    [InlineData("macos-itn-parity.jsonl", 2_084)]
    [InlineData("macos-itn-parity-holdout.jsonl", 3_756)]
    public void InverseTextNormalizerMatchesPinnedMacOracleByteForByte(
        string fileName,
        int expectedRows)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        var failures = new List<string>();
        var rowCount = 0;
        foreach (var line in File.ReadLines(path))
        {
            var row = JsonSerializer.Deserialize<MacParityRow>(line, SerializerOptions)
                ?? throw new InvalidOperationException($"Invalid parity row in {fileName}.");
            string actual;
            try
            {
                actual = InverseTextNormalizer.Normalize(row.Input, spokenPunctuation: true);
            }
            catch (RegexMatchTimeoutException)
            {
                // NAMES THE ROW INSTEAD OF DYING ANONYMOUSLY. This test failed once on CI with a
                // timeout thrown from inside the replace, and nothing in the output said which of
                // 3756 inputs had caused it - so the one thing needed to reproduce it was the one
                // thing the failure did not carry. Recorded as a failure like a mismatch, and the
                // loop continues, so a single slow row cannot hide every other difference behind it.
                // Ref: #91.
                if (failures.Count < 10)
                {
                    failures.Add(
                        $"[{row.Category}/{row.Slice}] row {rowCount + 1} TIMED OUT on input " +
                        $"'{row.Input}' ({row.Input.Length} chars)");
                }

                rowCount++;
                continue;
            }

            if (!string.Equals(row.Expected, actual, StringComparison.Ordinal) && failures.Count < 10)
            {
                failures.Add(
                    $"[{row.Category}/{row.Slice}] expected '{row.Expected}', got '{actual}'");
            }

            rowCount++;
        }

        Assert.Equal(expectedRows, rowCount);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task DefaultPipelineUsesBindingOrderAndContentFreeReceipts()
    {
        var pipeline = new DeterministicTextPipeline();
        var transcript = new Transcript(
            DictationSessionId.Create(),
            "um send an envy wisper thumbs up emoji period done",
            "whisper",
            DetectedLanguage: "en");
        var result = await pipeline.ProcessAsync(new DeterministicTextRequest(
            transcript,
            [new CustomWordEntry("envy wisper", "EnviousWispr")],
            new DeterministicTextOptions(true, true, true, true)));

        Assert.Equal(
            [
                DeterministicTextStage.CustomWords,
                DeterministicTextStage.FillerAndFalseStarts,
                DeterministicTextStage.SpokenEmoji,
                DeterministicTextStage.InverseTextNormalization,
                DeterministicTextStage.EmojiRestoration,
            ],
            result.Receipts.Select(receipt => receipt.Stage));
        Assert.Equal("send an EnviousWispr 👍. Done", result.Output.Text);
        Assert.False(result.IsDegraded);

        var receiptProperties = typeof(DeterministicStageReceipt).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("text", receiptProperties);
        Assert.DoesNotContain("transcript", receiptProperties);
        Assert.DoesNotContain("content", receiptProperties);
    }

    [Fact]
    public async Task PolishRunsAfterDeterministicWorkAndBeforeEmojiRestoration()
    {
        var pipeline = new DeterministicTextPipeline();
        var request = new DeterministicTextRequest(
            new Transcript(
                DictationSessionId.Create(),
                "thumbs up emoji we shipped it",
                "test",
                DetectedLanguage: "en"),
            [],
            new DeterministicTextOptions(false, false, true, false));
        var deterministic = await pipeline.ProcessAsync(request);

        var completed = await pipeline.ApplyPolishedTextAsync(
            request,
            deterministic,
            "We shipped it.");

        Assert.Equal("👍 We shipped it.", completed.Output.Text);
        Assert.Equal("👍 we shipped it", completed.DeterministicText);
        Assert.Equal(
            DeterministicStageStatus.Completed,
            completed.Receipts.Single(receipt =>
                receipt.Stage == DeterministicTextStage.EmojiRestoration).Status);
    }

    [Fact]
    public async Task FailedStageReturnsLastValidTextAndContinues()
    {
        IDeterministicTextStep[] steps =
        [
            new DelegateStep(
                DeterministicTextStage.CustomWords,
                context => context with { Text = "last valid" }),
            new DelegateStep(
                DeterministicTextStage.FillerAndFalseStarts,
                _ => throw new InvalidOperationException("synthetic failure")),
            new DelegateStep(
                DeterministicTextStage.InverseTextNormalization,
                context => context with { Text = context.Text + " output" }),
        ];
        var result = await new DeterministicTextPipeline(steps).ProcessAsync(CreateRequest("raw"));

        Assert.Equal("last valid output", result.Output.Text);
        Assert.True(result.IsDegraded);
        Assert.Equal(DeterministicStageStatus.Failed, result.Receipts[1].Status);
    }

    [Fact]
    public async Task TimedOutStageReturnsLastValidText()
    {
        // THE STAGE CANNOT FINISH UNTIL THE ASSERTIONS HAVE RUN. This used to race a 5 ms timer
        // against a 100 ms sleep, and on a loaded CI runner the timer callback arrived after the
        // sleep had returned - the stage "won" at 117 ms and the run on main went red for a change
        // in another file entirely. A stage held on a gate the test owns can only end one way.
        using var release = new ManualResetEventSlim(false);
        var step = new DelegateStep(
            DeterministicTextStage.CustomWords,
            context =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return context with { Text = "too late" };
            },
            TimeSpan.FromMilliseconds(5));
        try
        {
            var result = await new DeterministicTextPipeline([step]).ProcessAsync(CreateRequest("safe"));

            Assert.Equal("safe", result.Output.Text);
            Assert.True(result.IsDegraded);
            Assert.Equal(DeterministicStageStatus.TimedOut, Assert.Single(result.Receipts).Status);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task CallerCancellationStopsThePipeline()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new DeterministicTextPipeline().ProcessAsync(
                CreateRequest("safe"),
                cancellation.Token));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task CompletedStagePreservesEachPathsChangedComparison(
        bool restoration,
        bool changeText,
        bool changePolish)
    {
        var step = new DelegateStep(
            DeterministicTextStage.EmojiRestoration,
            context => context with
            {
                Text = changeText ? "changed text" : context.Text,
                PolishedText = changePolish ? "changed polish" : context.PolishedText,
            });

        var result = await RunStageAsync(restoration, step);

        Assert.Equal(changePolish ? "changed polish" : "polished input", result.Output.Text);
        Assert.Equal(!restoration && changeText ? "changed text" : "safe", result.DeterministicText);
        Assert.False(result.IsDegraded);
        var receipt = result.Receipts.Single(item => item.Stage == step.Stage);
        Assert.Equal(DeterministicStageStatus.Completed, receipt.Status);
        Assert.Equal(changePolish || (!restoration && changeText), receipt.Changed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ThrowingStagePreservesInputAndReportsFailure(bool restoration, bool timeout)
    {
        var step = new DelegateStep(
            DeterministicTextStage.EmojiRestoration,
            _ => throw (timeout
                ? new TimeoutException("synthetic timeout")
                : new InvalidOperationException("synthetic failure")));

        var result = await RunStageAsync(restoration, step);

        AssertStageFallback(result, timeout
            ? DeterministicStageStatus.TimedOut
            : DeterministicStageStatus.Failed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvalidStageResultPreservesInputAndReportsFailure(bool restoration, bool nullText)
    {
        var step = new DelegateStep(
            DeterministicTextStage.EmojiRestoration,
            context => nullText ? context with { Text = null!, PolishedText = "invalid polish" } : null!);

        var result = await RunStageAsync(restoration, step);

        AssertStageFallback(result, DeterministicStageStatus.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockingStageIsBusyUntilItsInvocationCompletes(bool restoration)
    {
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        var step = new DelegateStep(
            DeterministicTextStage.EmojiRestoration,
            context =>
            {
                Interlocked.Increment(ref invocations);
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
                return context with { Text = "finished text", PolishedText = "finished polish" };
            },
            TimeSpan.FromMilliseconds(250));
        var pipeline = new DeterministicTextPipeline([step]);
        Task<DeterministicTextContext>? worker = null;
        try
        {
            var pending = RunStageAsync(restoration, step, pipeline: pipeline);
            await entered.Task.WaitAsync(Patience);
            worker = GetOutstandingInvocation(pipeline, step);
            var result = await pending.WaitAsync(Patience);

            AssertStageFallback(result, DeterministicStageStatus.TimedOut);
            Assert.Equal(1, Volatile.Read(ref invocations));

            var busy = await RunStageAsync(restoration, step, pipeline: pipeline);

            AssertStageFallback(busy, DeterministicStageStatus.Busy);
            Assert.Equal(1, Volatile.Read(ref invocations));

            release.Set();
            var late = await worker.WaitAsync(Patience);
            Assert.Equal("finished text", late.Text);
            Assert.True(worker.IsCompletedSuccessfully);

            var recovered = await RunStageAsync(restoration, step, pipeline: pipeline);

            Assert.Equal("finished polish", recovered.Output.Text);
            Assert.Equal(restoration ? "safe" : "finished text", recovered.DeterministicText);
            Assert.False(recovered.IsDegraded);
            var receipt = recovered.Receipts.Single(item => item.Stage == step.Stage);
            Assert.Equal(DeterministicStageStatus.Completed, receipt.Status);
            Assert.True(receipt.Changed);
            Assert.Equal(2, Volatile.Read(ref invocations));
        }
        finally
        {
            release.Set();
            if (worker is not null)
            {
                await worker.WaitAsync(Patience);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CooperativeDeadlineCancellationReportsTimeoutAndWorkerCompletes(bool restoration)
    {
        using var release = new ManualResetEventSlim(false);
        using var allowExit = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        // THE DEADLINE IS LONGER THAN THE POOL'S WORST DAY, AND THE STEP SAYS WHEN IT IS IN. The
        // executor hands the worker to the thread pool with the deadline's own token, so a deadline
        // that elapses before the pool has started the worker cancels the task without running it -
        // correct for a stage (a stage that was never reached before its deadline is TimedOut, not
        // run late), and fatal for this test, whose whole subject is a worker that IS running when
        // the deadline lands. At 250 ms a starved hosted runner hit exactly that three times in one
        // day, and the wait for the cancel below never ended (#165); reproduced locally by capping
        // the pool and filling it past the deadline, where the worker never entered. The step now
        // reports that it has entered, the test waits for that before it expects anything, and the
        // deadline is long enough that entering first is the overwhelmingly likely order without
        // being long enough to make the test slow.
        var step = new DelegateStep(
            DeterministicTextStage.EmojiRestoration,
            (context, token) =>
            {
                Interlocked.Increment(ref invocations);
                entered.TrySetResult();
                try
                {
                    release.Wait(token);
                    return context;
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult(token);
                    Assert.True(allowExit.Wait(Patience, CancellationToken.None));
                    throw;
                }
            },
            TimeSpan.FromSeconds(2));
        var pipeline = new DeterministicTextPipeline([step]);
        try
        {
            var pending = RunStageAsync(restoration, step, pipeline: pipeline);
            await entered.Task.WaitAsync(Patience);
            var token = await cancelled.Task.WaitAsync(Patience);
            var worker = GetOutstandingInvocation(pipeline, step);
            var result = await pending.WaitAsync(Patience);

            AssertStageFallback(result, DeterministicStageStatus.TimedOut);
            Assert.True(token.IsCancellationRequested);
            // WaitHandle throws on a disposed source where IsCancellationRequested does not, so this is
            // the assertion that the abandoned worker still owns a live token.
            Assert.True(token.WaitHandle.WaitOne(0));
            Assert.Equal(1, Volatile.Read(ref invocations));

            allowExit.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(Patience));
            Assert.True(worker.IsCanceled);
        }
        finally
        {
            allowExit.Set();
            release.Set();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineOperationCanceledExceptionIsReportedAsTimeout(bool restoration)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        var step = new DelegateStep(
            DeterministicTextStage.EmojiRestoration,
            (_, token) =>
            {
                Interlocked.Increment(ref invocations);
                try
                {
                    using var release = new ManualResetEventSlim(false);
                    release.Wait(token);
                    throw new InvalidOperationException("The cancellation wait unexpectedly returned.");
                }
                finally
                {
                    finished.SetResult();
                }
            },
            TimeSpan.FromMilliseconds(250));

        var result = await RunStageAsync(restoration, step).WaitAsync(Patience);
        await finished.Task.WaitAsync(Patience);

        AssertStageFallback(result, DeterministicStageStatus.TimedOut);
        Assert.True(finished.Task.IsCompletedSuccessfully);
        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task ConcurrentRequestsAcrossBothPathsStartOnlyOneInvocation()
    {
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        var step = new DelegateStep(
            DeterministicTextStage.EmojiRestoration,
            context =>
            {
                Interlocked.Increment(ref invocations);
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
                return context with { PolishedText = "finished polish" };
            },
            TimeSpan.FromSeconds(30));
        var pipeline = new DeterministicTextPipeline([step]);
        var attempts = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            await start.Task;
            return await RunStageAsync(index % 2 == 0, step, pipeline: pipeline);
        })).ToList();
        try
        {
            start.SetResult();
            await entered.Task.WaitAsync(Patience);
            for (var index = 0; index < 15; index++)
            {
                var completed = await Task.WhenAny(attempts).WaitAsync(Patience);
                attempts.Remove(completed);
                AssertStageFallback(await completed, DeterministicStageStatus.Busy);
                Assert.Equal(1, Volatile.Read(ref invocations));
            }

            release.Set();
            var winner = await Assert.Single(attempts).WaitAsync(Patience);
            Assert.Equal("finished polish", winner.Output.Text);
            Assert.Equal("safe", winner.DeterministicText);
            Assert.False(winner.IsDegraded);
            Assert.Equal(DeterministicStageStatus.Completed,
                winner.Receipts.Single(item => item.Stage == step.Stage).Status);
            Assert.Equal(1, Volatile.Read(ref invocations));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(attempts).WaitAsync(Patience);
        }
    }

    // The actual executor task, so worker completion is established before a recovery request; a
    // signal from inside Process can fire before Task.Run itself has reached a terminal state.
    private static Task<DeterministicTextContext> GetOutstandingInvocation(
        DeterministicTextPipeline pipeline, IDeterministicTextStep step)
    {
        var invocation = pipeline.OutstandingInvocation(step);
        Assert.NotNull(invocation);
        return invocation;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallerCancellationDuringStagePropagates(bool restoration, bool cooperative)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        CancellationToken stepToken = default;
        var step = new DelegateStep(
            DeterministicTextStage.EmojiRestoration,
            (context, token) =>
            {
                Interlocked.Increment(ref invocations);
                stepToken = token;
                entered.SetResult();
                try
                {
                    Assert.True(release.Wait(TimeSpan.FromSeconds(30), cooperative ? token : CancellationToken.None));
                    return context with { Text = "too late", PolishedText = "too late" };
                }
                finally
                {
                    finished.SetResult();
                }
            },
            TimeSpan.FromSeconds(30));
        try
        {
            var pending = RunStageAsync(restoration, step, cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            cancellation.Cancel();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.True(stepToken.IsCancellationRequested);
            if (!cooperative)
            {
                Assert.True(stepToken.WaitHandle.WaitOne(0));
            }

            Assert.Equal(1, Volatile.Read(ref invocations));
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(finished.Task.IsCompletedSuccessfully);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentStageCancellationFallsBack(bool restoration)
    {
        // Neither the caller nor the deadline asked; the step stopped short on its own. That is a
        // failed stage, and a failed stage returns the last valid text.
        var step = new DelegateStep(
            DeterministicTextStage.EmojiRestoration,
            _ => throw new OperationCanceledException("independent"));

        AssertStageFallback(await RunStageAsync(restoration, step), DeterministicStageStatus.Failed);
    }

    [Theory]
    [InlineData(false, "stack-overflow")]
    [InlineData(true, "stack-overflow")]
    [InlineData(false, "out-of-memory")]
    [InlineData(true, "out-of-memory")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201:Do not raise reserved exception types",
        Justification = "Synthetic failures verify the exception filter without exhausting memory or the stack.")]
    public async Task ExcludedStageExceptionsPropagate(bool restoration, string failure)
    {
        Exception expected = failure switch
        {
            "stack-overflow" => new StackOverflowException("synthetic stack overflow"),
            "out-of-memory" => new OutOfMemoryException("synthetic out of memory"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var step = new DelegateStep(DeterministicTextStage.EmojiRestoration, _ => throw expected);

        var actual = await Assert.ThrowsAsync(expected.GetType(), () => RunStageAsync(restoration, step));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task SuccessfulRestorationPreservesEarlierDegradation()
    {
        var step = new DelegateStep(
            DeterministicTextStage.EmojiRestoration,
            context => context with { PolishedText = "restored polish" });

        var result = await RunStageAsync(true, step, alreadyDegraded: true);

        Assert.Equal("restored polish", result.Output.Text);
        Assert.Equal("safe", result.DeterministicText);
        Assert.True(result.IsDegraded);
        var receipt = result.Receipts.Single(item => item.Stage == step.Stage);
        Assert.Equal(DeterministicStageStatus.Completed, receipt.Status);
        Assert.True(receipt.Changed);
    }

    private static async Task<DeterministicTextResult> RunStageAsync(
        bool restoration,
        IDeterministicTextStep step,
        bool alreadyDegraded = false,
        DeterministicTextPipeline? pipeline = null,
        CancellationToken cancellationToken = default)
    {
        pipeline ??= new DeterministicTextPipeline([step]);
        var request = CreateRequest("safe") with { PolishedText = "polished input" };
        if (!restoration)
        {
            var result = await pipeline.ProcessAsync(request, cancellationToken);
            Assert.Equal(DeterministicTextStage.EmojiRestoration, Assert.Single(result.Receipts).Stage);
            return result;
        }

        // A MIDDLE RECEIPT EXPOSES APPEND-OR-REORDER REGRESSIONS. The neighboring receipts must
        // survive by identity while restoration replaces only its own earlier result.
        DeterministicStageReceipt[] receipts =
        [
            new(DeterministicTextStage.CustomWords, DeterministicStageStatus.Completed, true, 11),
            new(DeterministicTextStage.EmojiRestoration, DeterministicStageStatus.Skipped, false, 0),
            new(DeterministicTextStage.InverseTextNormalization, DeterministicStageStatus.Completed, false, 13),
        ];
        var deterministic = new DeterministicTextResult(
            new ProcessedText(request.Transcript.SessionId, "safe"), receipts, alreadyDegraded);

        var restored = await pipeline.ApplyPolishedTextAsync(
            request, deterministic, "polished input", cancellationToken);

        Assert.Equal(
            [
                DeterministicTextStage.CustomWords,
                DeterministicTextStage.EmojiRestoration,
                DeterministicTextStage.InverseTextNormalization,
            ],
            restored.Receipts.Select(receipt => receipt.Stage));
        Assert.Same(receipts[0], restored.Receipts[0]);
        Assert.NotSame(receipts[1], restored.Receipts[1]);
        Assert.Same(receipts[2], restored.Receipts[2]);
        Assert.Equal(DeterministicStageStatus.Skipped, deterministic.Receipts[1].Status);
        return restored;
    }

    private static void AssertStageFallback(DeterministicTextResult result, DeterministicStageStatus status)
    {
        Assert.Equal("polished input", result.Output.Text);
        Assert.Equal("safe", result.DeterministicText);
        Assert.True(result.IsDegraded);
        var receipt = result.Receipts.Single(item => item.Stage == DeterministicTextStage.EmojiRestoration);
        Assert.Equal(status, receipt.Status);
        Assert.False(receipt.Changed);
    }

    [Fact]
    public void EmojiDictionaryLoadsAndFormatterIsIdempotent()
    {
        var formatter = SpokenEmojiFormatter.LoadBundled();
        var once = formatter.Format("thumbs up emoji and rocket emoji");

        Assert.Equal("👍 and 🚀", once);
        Assert.Equal(once, formatter.Format(once));
    }

    [Theory]
    [InlineData("thumbs up emoji", "👍")]
    [InlineData("send a fire emoji", "send a 🔥")]
    [InlineData("rocket emoji we shipped it", "🚀 we shipped it")]
    [InlineData("smiling face emoticon thanks", "🙂 thanks")]
    [InlineData("THUMBS UP EMOJI!", "👍!")]
    [InlineData("happy birthday Emma red heart emoji", "happy birthday Emma ❤️")]
    [InlineData("thumbs up, emoji", "👍")]
    [InlineData("thumbs up — emoji", "👍")]
    [InlineData("\"thumbs up emoji\"", "\"👍\"")]
    [InlineData("thumbs up emoji and rocket emoji", "👍 and 🚀")]
    [InlineData("sod face emoji", "😢")]
    [InlineData("sod emoji", "😢")]
    public void SpokenEmojiFormatterMatchesMacPositiveCases(string input, string expected)
    {
        Assert.Equal(expected, SpokenEmojiFormatter.LoadBundled().Format(input));
    }

    [Theory]
    [InlineData("I want a rocket ride to the moon")]
    [InlineData("I sent three thumbs up emojis to the team")]
    [InlineData("the red heart emoji category is confusing")]
    [InlineData("the fire emoji feature is great")]
    [InlineData("the smiling face emoji symbol")]
    [InlineData("the red heart emoji meaning is universal")]
    [InlineData("thumbs 👍 up emoji")]
    public void SpokenEmojiFormatterDeclinesUnsafeOrLiteralCases(string input)
    {
        Assert.Equal(input, SpokenEmojiFormatter.LoadBundled().Format(input));
    }

    [Fact]
    public void EmojiRestorerReinsertsDroppedGlyphWithoutChangingKeptGlyphs()
    {
        var restored = EmojiRestorer.Restore(
            "Ship it today.",
            "Ship it 🚀 today.");
        var kept = EmojiRestorer.Restore(
            "Ship it 🚀 today.",
            "Ship it 🚀 today.");

        Assert.Equal("Ship it 🚀 today.", restored.Text);
        Assert.Equal(1, restored.Dropped);
        Assert.Equal(1, restored.Restored);
        Assert.Equal("Ship it 🚀 today.", kept.Text);
        Assert.Equal(0, kept.Dropped);
    }

    public static TheoryData<string, string, string> EmojiRestoreParityCases => new()
    {
        { "Shipped it.", "Shipped it 🚀.", "Shipped it 🚀." },
        { "Wait, that is wrong.", "👀 wait that is wrong.", "👀 Wait, that is wrong." },
        { "This launch is huge.", "This launch 🔥🔥🔥 is huge.", "This launch 🔥🔥🔥 is huge." },
        { "Miami trip.", "Miami ☀️ 🌴 trip.", "Miami ☀️ 🌴 trip." },
        { "Très bien, fini le projet.", "Très bien 🎉 fini le projet.", "Très bien 🎉, fini le projet." },
        { "Check example.com for details.", "Check example.com 🔥 for details.", "Check example.com 🔥 for details." },
        { "First we update the dashboard, and the dashboard shows metrics.", "First we update the dashboard 🔥. The dashboard shows metrics.", "First we update the dashboard 🔥, and the dashboard shows metrics." },
        { "I shipped the auth refactor. The batch job is next.", "I shipped the auth refactor and the batch job is next 🚀", "I shipped the auth refactor. The batch job is next 🚀." },
        { "We shipped it. Users love it.", "We shipped it 🚀 and users love it.", "We shipped it 🚀. Users love it." },
        { "Actually, is more accurate.", "Actually, 😢 is more accurate.", "Actually, 😢 is more accurate." },
        { "Mike, can you take the Figma review? Link is in the channel.", "Mike can you take the Figma review 👍 link is in the channel", "Mike, can you take the Figma review? 👍 Link is in the channel." },
        { "Conversion hit 12.5%.", "Conversion hit 12.5% 🔥", "Conversion hit 12.5% 🔥." },
        { "The launch went well and the demo 🔥 crushed it.", "The launch 🔥 went well and the demo 🔥 crushed it.", "The launch 🔥 went well and the demo 🔥 crushed it." },
        { "Happy birthday, bro 🎉.", "Happy birthday bro 🎉 🎂.", "Happy birthday, bro 🎉 🎂." },
        { "Party A 🎉 🚀 B end.", "Party A 🎉 🎂 🚀 B end.", "Party A 🎉 🎂 🚀 B end." },
        { "Done.", "Done. 🚀", "Done. 🚀" },
        { "I really think I can’t.", "I really think I can't 🔥.", "I really think I can’t 🔥." },
        { "", "hi 🔥", "🔥" },
        { "Done. Onto the next thing.", "Done. 🚀 onto the next thing.", "Done. 🚀 Onto the next thing." },
    };

    [Theory]
    [MemberData(nameof(EmojiRestoreParityCases))]
    public void EmojiRestorerMatchesPinnedMacPlacementCases(
        string polished,
        string prePolish,
        string expected)
    {
        var result = EmojiRestorer.Restore(polished, prePolish);

        Assert.Equal(expected, result.Text);
        Assert.Equal(result.Dropped, result.Restored);
    }

    [Fact]
    public void EmojiRestorerTreatsPresentationAndSkinToneVariantsAsKept()
    {
        var presentation = EmojiRestorer.Restore("Love you ❤ so much.", "Love you ❤️ so much.");
        var skinTone = EmojiRestorer.Restore("Nice work 👍 everyone.", "Nice work 👍🏽 everyone.");

        Assert.Equal(0, presentation.Dropped);
        Assert.Equal("Love you ❤ so much.", presentation.Text);
        Assert.Equal(0, skinTone.Dropped);
        Assert.Equal("Nice work 👍 everyone.", skinTone.Text);
    }

    [Fact]
    public void CustomWordFuzzyFallbackAndHardeningThresholdsMatchMacContract()
    {
        var result = CustomWordCorrector.Correct(
            "deployed to kuberntes",
            [new CustomWordEntry("Kubernetes", "Kubernetes")]);

        Assert.Equal("deployed to Kubernetes", result.Text);
        Assert.Equal(1, result.ReplacementCount);
        Assert.Equal(0, CustomWordCorrector.LargeVocabularyPenalty(101));
        Assert.Equal(0.02, CustomWordCorrector.LargeVocabularyPenalty(600), precision: 5);
        Assert.Equal(0.06, CustomWordCorrector.LargeVocabularyPenalty(5_000), precision: 5);
        Assert.Equal(0.04, CustomWordCorrector.LengthAwareAdjustment(16), precision: 5);
    }

    private static DeterministicTextRequest CreateRequest(ParityCase item) => new(
        new Transcript(
            DictationSessionId.Create(),
            item.Input,
            item.EngineId,
            DetectedLanguage: item.Language),
        item.CustomWords,
        item.Options);

    private static DeterministicTextRequest CreateRequest(string text) => new(
        new Transcript(DictationSessionId.Create(), text, "test", DetectedLanguage: "en"),
        [],
        new DeterministicTextOptions(false, false, false, false));

    private static async Task<IReadOnlyList<ParityCase>> LoadParityCasesAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "deterministic-parity.json");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<ParityCase>>(stream, SerializerOptions)
            ?? throw new InvalidOperationException("The deterministic parity fixture is invalid.");
    }

    private sealed record ParityCase(
        string Name,
        string Category,
        string Input,
        string Expected,
        string? Language,
        string EngineId,
        IReadOnlyList<CustomWordEntry> CustomWords,
        DeterministicTextOptions Options,
        string? Difference = null);

    private sealed record MacParityRow(
        string Input,
        string Expected,
        string Category,
        string Slice);

    private sealed class DelegateStep(
        DeterministicTextStage stage,
        Func<DeterministicTextContext, CancellationToken, DeterministicTextContext> process,
        TimeSpan? timeout = null) : IDeterministicTextStep
    {
        public DelegateStep(
            DeterministicTextStage stage,
            Func<DeterministicTextContext, DeterministicTextContext> process,
            TimeSpan? timeout = null)
            : this(stage, (context, _) => process(context), timeout)
        {
        }

        public DeterministicTextStage Stage => stage;

        public TimeSpan Timeout => timeout ?? TimeSpan.FromSeconds(1);

        public bool IsEnabled(DeterministicTextContext context) => true;

        public DeterministicTextContext Process(DeterministicTextContext context, CancellationToken cancellationToken) =>
            process(context, cancellationToken);
    }
}
