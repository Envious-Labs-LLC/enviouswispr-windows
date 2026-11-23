namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The two things a test of a loop needs in order never to wait on the wall clock: a count it can be
/// told when a milestone is reached, and a clock that moves only when it is moved. Shared by the tests
/// of the pipeline's controllers.
/// </summary>
internal static class Deterministic
{
    /// <summary>
    /// A count that a test can wait to reach. The check and the registration happen under one lock,
    /// and so do the increment and the choice of whom to wake, so a milestone crossed between "is it
    /// there yet" and "tell me when it is" cannot be missed. Waiters are woken outside the lock.
    /// </summary>
    internal sealed class Milestone
    {
        private readonly object _lock = new();
        private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];
        private int _count;

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _count;
                }
            }
        }

        public Task WhenAtLeast(int count)
        {
            lock (_lock)
            {
                if (_count >= count)
                {
                    return Task.CompletedTask;
                }

                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, reached));
                return reached.Task;
            }
        }

        public void Increment()
        {
            List<TaskCompletionSource> due;
            lock (_lock)
            {
                _count++;
                due = _waiters.Where(waiter => waiter.Count <= _count).Select(waiter => waiter.Reached).ToList();
                _waiters.RemoveAll(waiter => waiter.Count <= _count);
            }

            foreach (var reached in due)
            {
                reached.TrySetResult();
            }
        }
    }

    /// <summary>
    /// A clock that moves only when told to. `Task.Delay` on it becomes a timer this clock owns, fired
    /// in due order as time is advanced, so the loop's quarter-second re-ask and its cadence are
    /// crossed by a test in one call rather than waited for.
    ///
    /// ADVANCING FIRES THE TIMER; IT DOES NOT RUN THE LOOP. The delay's continuation may be posted
    /// rather than run inline (the test framework installs a synchronization context), so a test
    /// that advances and then reads a count is racing. Positive steps are awaited on a milestone the
    /// loop crosses - a snapshot taken, a preview shown, a timer registered - and negative ones read
    /// <see cref="NextDue"/>, which is what the loop asked the clock for and cannot be raced.
    /// </summary>
    internal sealed class ManualClock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly Milestone _registered = new();
        private long _ticks;

        /// <summary>How far from now the earliest registered timer is due, or null when none is.</summary>
        public TimeSpan? NextDue
        {
            get
            {
                lock (_lock)
                {
                    return _timers.Count == 0 ? null : TimeSpan.FromTicks(_timers.Min(timer => timer.Due) - _ticks);
                }
            }
        }

        /// <summary>Completes once at least <paramref name="count"/> timers have been registered.</summary>
        public Task WhenRegistered(int count) => _registered.WhenAtLeast(count);

        /// <summary>How many timers have been registered so far, for a wait that means "one more than now".</summary>
        public int Registered => _registered.Count;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_lock)
            {
                return _ticks;
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return Start.AddTicks(_ticks);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var target = GetTimestamp() + by.Ticks;
            while (true)
            {
                ManualTimer? due;
                lock (_lock)
                {
                    due = _timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                    if (due is null)
                    {
                        _ticks = target;
                        break;
                    }

                    _ticks = due.Due;
                    _timers.Remove(due);
                }

                due.Fire();
            }
        }

        private void Schedule(ManualTimer timer, long due)
        {
            lock (_lock)
            {
                _timers.Remove(timer);
                timer.Due = due;
                _timers.Add(timer);
            }

            _registered.Increment();
        }

        private void Cancel(ManualTimer timer)
        {
            lock (_lock)
            {
                _timers.Remove(timer);
            }
        }

        internal sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    clock.Cancel(this);
                }
                else
                {
                    clock.Schedule(this, clock.GetTimestamp() + dueTime.Ticks);
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose() => clock.Cancel(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// A scheduler that can withhold queued tasks from running until explicitly drained, reproducing
    /// thread-pool starvation deterministically without altering process-wide pool limits.
    /// </summary>
    internal sealed class ControlledTaskScheduler : TaskScheduler
    {
        private readonly object _lock = new();
        private readonly Queue<Task> _held = new();
        private readonly Milestone _queued = new();
        private int _holdRemaining;

        public ControlledTaskScheduler(int holdCount = 0)
        {
            _holdRemaining = holdCount;
        }

        public void HoldNext(int count = 1)
        {
            lock (_lock)
            {
                _holdRemaining += count;
            }
        }

        public Task WhenQueued(int count) => _queued.WhenAtLeast(count);

        public int QueuedCount => _queued.Count;

        public Task<TaskStatus[]> DrainHeldAsync()
        {
            Task[] tasks;
            lock (_lock)
            {
                tasks = [.. _held];
                _held.Clear();
            }

            return Task.Run(() => tasks.Select(task =>
            {
                TryExecuteTask(task);
                return task.Status;
            }).ToArray());
        }

        protected override IEnumerable<Task>? GetScheduledTasks()
        {
            lock (_lock)
            {
                return _held.ToArray();
            }
        }

        protected override void QueueTask(Task task)
        {
            var hold = false;
            lock (_lock)
            {
                if (_holdRemaining > 0)
                {
                    _holdRemaining--;
                    _held.Enqueue(task);
                    hold = true;
                }
            }

            _queued.Increment();
            if (!hold)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static state =>
                    {
                        var (scheduler, queuedTask) = ((ControlledTaskScheduler, Task))state!;
                        scheduler.TryExecuteTask(queuedTask);
                    },
                    (this, task),
                    preferLocal: false);
            }
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }
}
