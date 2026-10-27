using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Rollouts;

namespace FleetPulse.UnitTests;

public sealed class RolloutStateMachineTests
{
    public static TheoryData<RolloutStatus, RolloutTrigger, RolloutStatus?> TransitionCases
    {
        get
        {
            var allowed = new Dictionary<(RolloutStatus, RolloutTrigger), RolloutStatus>
            {
                [(RolloutStatus.Draft, RolloutTrigger.Start)] = RolloutStatus.WaveRunning,
                [(RolloutStatus.WaveRunning, RolloutTrigger.WaveFinished)] = RolloutStatus.GateCheck,
                [(RolloutStatus.GateCheck, RolloutTrigger.GatePassed)] = RolloutStatus.WaveRunning,
                [(RolloutStatus.GateCheck, RolloutTrigger.GateFailed)] = RolloutStatus.Halted,
                [(RolloutStatus.Halted, RolloutTrigger.BeginRollback)] = RolloutStatus.RollingBack,
                [(RolloutStatus.RollingBack, RolloutTrigger.RollbackFinished)] = RolloutStatus.RolledBack
            };
            var cases = new TheoryData<RolloutStatus, RolloutTrigger, RolloutStatus?>();
            foreach (var state in Enum.GetValues<RolloutStatus>())
            foreach (var trigger in Enum.GetValues<RolloutTrigger>())
            {
                cases.Add(state, trigger, allowed.TryGetValue((state, trigger), out var next) ? next : null);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(TransitionCases))]
    public void OnlyAllowedEdgesChangeState(RolloutStatus state, RolloutTrigger trigger,
        RolloutStatus? expected)
    {
        var clock = new TestClock();
        var machine = new RolloutStateMachine(clock);
        var rollout = AtState(state, machine);
        Assert.Equal(state, rollout.Status);
        var previousVersion = rollout.TransitionVersion;
        var previousReason = rollout.StateReason;
        var previousWave = rollout.CurrentWaveNumber;
        var wave = IsWaveTrigger(trigger) ? previousWave : null;

        var applied = machine.TryApply(rollout, trigger, "Requested transition", wave);

        Assert.Equal(expected is not null, applied);
        Assert.Equal(expected ?? state, rollout.Status);
        Assert.Equal(previousVersion + (applied ? 1 : 0), rollout.TransitionVersion);
        Assert.Equal(rollout.TransitionVersion, rollout.Transitions.Count);
        if (!applied)
        {
            Assert.Equal(previousReason, rollout.StateReason);
            Assert.Equal(previousWave, rollout.CurrentWaveNumber);
        }
        else
        {
            var transition = rollout.Transitions.Last();
            Assert.Equal(state, transition.FromStatus);
            Assert.Equal(expected, transition.ToStatus);
            Assert.Equal("Requested transition", transition.Reason);
            Assert.Equal(clock.UtcNow, transition.OccurredAtUtc);
        }
    }

    [Fact]
    public void EveryWaveMustReachItsGateBeforeCompletion()
    {
        var clock = new TestClock();
        var machine = new RolloutStateMachine(clock);
        var rollout = NewRollout(1, 1, 1, 1);
        Assert.True(machine.TryApply(rollout, RolloutTrigger.Start, "  Approved  "));
        Assert.Equal("Approved", rollout.StateReason);

        for (var number = 1; number <= 4; number++)
        {
            Assert.Equal(number, rollout.CurrentWaveNumber);
            Assert.False(machine.TryApply(rollout, RolloutTrigger.GatePassed, "Too early", number));
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            Assert.True(machine.TryApply(rollout, RolloutTrigger.WaveFinished, "Wave finished", number));
            Assert.Equal(RolloutStatus.GateCheck, rollout.Status);
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            Assert.True(machine.TryApply(rollout, RolloutTrigger.GatePassed, "Gate passed", number));
            Assert.Equal(number == 4 ? RolloutStatus.Completed : RolloutStatus.WaveRunning, rollout.Status);
            Assert.Equal(clock.UtcNow, rollout.Transitions.Last().OccurredAtUtc);
        }

        Assert.Equal(Enumerable.Range(1, 9), rollout.Transitions.Select(transition => transition.Sequence));
        var firstGate = rollout.Transitions.ElementAt(2);
        Assert.Equal(1, firstGate.FromWaveNumber);
        Assert.Equal(2, firstGate.ToWaveNumber);
        Assert.All(rollout.Transitions, transition => Assert.Equal(rollout.Id, transition.RolloutId));
    }

    [Fact]
    public void FailureHaltsBeforeRollbackAndPreservesTheFailureReason()
    {
        var clock = new TestClock();
        var machine = new RolloutStateMachine(clock);
        var rollout = AtState(RolloutStatus.GateCheck, machine);

        Assert.True(machine.TryApply(rollout, RolloutTrigger.GateFailed, "Wave health gate failed", 1));
        Assert.Equal(RolloutStatus.Halted, rollout.Status);
        Assert.False(machine.TryApply(rollout, RolloutTrigger.Start, "Resume is not supported"));
        Assert.True(machine.TryApply(rollout, RolloutTrigger.BeginRollback, "Restoring updated devices"));
        Assert.True(machine.TryApply(rollout, RolloutTrigger.RollbackFinished, "Rollback complete"));
        Assert.Equal(RolloutStatus.RolledBack, rollout.Status);
        Assert.Equal("Rollback complete", rollout.StateReason);
        Assert.Contains(rollout.Transitions, transition => transition.ToStatus == RolloutStatus.Halted &&
            transition.Reason == "Wave health gate failed");
    }

    [Theory]
    [InlineData(1, 0, 0, 0, null)]
    [InlineData(1, 0, 0, 1, 4)]
    public void EmptyWavesAreSkipped(int first, int second, int third, int fourth, int? next)
    {
        var machine = new RolloutStateMachine(new TestClock());
        var rollout = NewRollout(first, second, third, fourth);
        Assert.True(machine.TryApply(rollout, RolloutTrigger.Start, "Approved"));
        Assert.True(machine.TryApply(rollout, RolloutTrigger.WaveFinished, "Wave finished", 1));
        Assert.True(machine.TryApply(rollout, RolloutTrigger.GatePassed, "Gate passed", 1));
        Assert.Equal(next is null ? RolloutStatus.Completed : RolloutStatus.WaveRunning, rollout.Status);
        Assert.Equal(next ?? 1, rollout.CurrentWaveNumber);
    }

    [Fact]
    public void StaleDuplicateAndMissingWaveSignalsDoNotAddHistory()
    {
        var machine = new RolloutStateMachine(new TestClock());
        var rollout = AtState(RolloutStatus.GateCheck, machine);
        Assert.True(machine.TryApply(rollout, RolloutTrigger.GatePassed, "First gate passed", 1));
        Assert.True(machine.TryApply(rollout, RolloutTrigger.WaveFinished, "Second wave finished", 2));
        var version = rollout.TransitionVersion;

        Assert.False(machine.TryApply(rollout, RolloutTrigger.GatePassed, "Old gate", 1));
        Assert.False(machine.TryApply(rollout, RolloutTrigger.GateFailed, "Old failure", 1));
        Assert.False(machine.TryApply(rollout, RolloutTrigger.GatePassed, "Missing wave"));
        Assert.False(machine.TryApply(rollout, RolloutTrigger.GatePassed, "Future wave", 3));
        Assert.Equal(version, rollout.TransitionVersion);
        Assert.Equal(version, rollout.Transitions.Count);
        Assert.Equal(2, rollout.CurrentWaveNumber);
    }

    [Fact]
    public void InvalidReasonsAndUnknownTriggersCannotMutateTheRollout()
    {
        var machine = new RolloutStateMachine(new TestClock());
        var rollout = NewRollout(1, 0, 0, 0);
        foreach (var reason in new[] { null, "", "  ", new string('x', 513) })
        {
            Assert.ThrowsAny<ArgumentException>(() => machine.TryApply(rollout, RolloutTrigger.Start, reason!));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => machine.TryApply(rollout, (RolloutTrigger)99, "Unknown"));
        Assert.Equal(RolloutStatus.Draft, rollout.Status);
        Assert.Empty(rollout.Transitions);
        Assert.True(machine.TryApply(rollout, RolloutTrigger.Start, new string('x', 512)));
        Assert.Equal(512, Assert.Single(rollout.Transitions).Reason.Length);
    }

    [Fact]
    public void EmptyPlanCannotStart()
    {
        var rollout = NewRollout(0, 0, 0, 0);
        Assert.False(new RolloutStateMachine(new TestClock()).TryApply(rollout, RolloutTrigger.Start, "Approved"));
        Assert.Equal(RolloutStatus.Draft, rollout.Status);
        Assert.Empty(rollout.Transitions);
    }

    private static bool IsWaveTrigger(RolloutTrigger trigger) => trigger is
        RolloutTrigger.WaveFinished or RolloutTrigger.GatePassed or RolloutTrigger.GateFailed;

    private static Rollout AtState(RolloutStatus state, RolloutStateMachine machine)
    {
        var rollout = NewRollout(1, 1, 1, 1);
        if (state == RolloutStatus.Draft) return rollout;
        machine.TryApply(rollout, RolloutTrigger.Start, "Approved");
        if (state == RolloutStatus.WaveRunning) return rollout;
        machine.TryApply(rollout, RolloutTrigger.WaveFinished, "Wave finished", 1);
        if (state == RolloutStatus.GateCheck) return rollout;
        if (state == RolloutStatus.Completed)
        {
            for (var number = 1; number <= 4; number++)
            {
                machine.TryApply(rollout, RolloutTrigger.GatePassed, "Gate passed", number);
                if (number < 4) machine.TryApply(rollout, RolloutTrigger.WaveFinished, "Wave finished", number + 1);
            }
            return rollout;
        }
        machine.TryApply(rollout, RolloutTrigger.GateFailed, "Gate failed", 1);
        if (state == RolloutStatus.Halted) return rollout;
        machine.TryApply(rollout, RolloutTrigger.BeginRollback, "Restore devices");
        if (state == RolloutStatus.RollingBack) return rollout;
        machine.TryApply(rollout, RolloutTrigger.RollbackFinished, "Devices restored");
        return rollout;
    }

    private static Rollout NewRollout(params int[] sizes)
    {
        var rollout = new Rollout { Id = RolloutId.New() };
        for (var index = 0; index < sizes.Length; index++)
        {
            var wave = new Wave { Id = WaveId.New(), RolloutId = rollout.Id, Number = index + 1 };
            for (var device = 0; device < sizes[index]; device++) wave.Targets.Add(new WaveTarget { DeviceId = DeviceId.New() });
            rollout.Waves.Add(wave);
        }
        return rollout;
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    }
}
