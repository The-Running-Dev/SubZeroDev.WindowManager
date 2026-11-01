namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S3: the restart budget and the flood meter.</summary>
public sealed class FaultPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void S3_3_the_first_three_faults_restart_after_1_5_and_30_seconds()
    {
        var budget = new RestartBudget(new RestartPolicy(3, 300), SupervisorTimings.Default.Backoffs);

        Assert.Equal(TimeSpan.FromSeconds(1), budget.RecordFault(Start));
        Assert.Equal(TimeSpan.FromSeconds(5), budget.RecordFault(Start.AddSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(30), budget.RecordFault(Start.AddSeconds(20)));
    }

    [Fact]
    public void S3_4_the_fourth_fault_in_the_window_spends_the_budget_and_reset_restores_it()
    {
        var budget = new RestartBudget(new RestartPolicy(3, 300), SupervisorTimings.Default.Backoffs);
        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(budget.RecordFault(Start.AddSeconds(i)));
        }

        Assert.Null(budget.RecordFault(Start.AddSeconds(60)));
        Assert.Equal(4, budget.Within(Start.AddSeconds(60)).Count);

        budget.Reset();

        Assert.Empty(budget.Within(Start.AddSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(1), budget.RecordFault(Start.AddSeconds(61)));
    }

    [Fact]
    public void S3_4_faults_older_than_the_window_do_not_count()
    {
        var budget = new RestartBudget(new RestartPolicy(3, 300), SupervisorTimings.Default.Backoffs);
        for (var i = 0; i < 3; i++)
        {
            budget.RecordFault(Start.AddSeconds(i));
        }

        Assert.Single(budget.Within(Start.AddSeconds(301)));
        Assert.Equal(TimeSpan.FromSeconds(5), budget.RecordFault(Start.AddSeconds(301)));
    }

    [Fact]
    public void S3_7_messages_within_the_rate_pass()
    {
        long now = 0;
        var meter = new FloodMeter(200, TimeSpan.FromSeconds(10), () => now);

        for (var second = 0; second < 20; second++)
        {
            for (var i = 0; i < 200; i++)
            {
                now = second * 1000 + i * 4;
                Assert.Equal(FloodVerdict.Within, meter.Record());
            }
        }
    }

    [Fact]
    public void S3_7_a_short_burst_is_over_but_not_a_flood()
    {
        long now = 0;
        var meter = new FloodMeter(200, TimeSpan.FromSeconds(10), () => now);
        var verdicts = Enumerable.Range(0, 1000).Select(_ => meter.Record()).ToList();

        Assert.Equal(200, verdicts.Count(verdict => verdict == FloodVerdict.Within));
        Assert.Equal(800, verdicts.Count(verdict => verdict == FloodVerdict.Over));
    }

    [Fact]
    public void S3_7_sustained_traffic_over_the_rate_is_a_flood_after_the_sustain()
    {
        long now = 0;
        var meter = new FloodMeter(200, TimeSpan.FromSeconds(10), () => now);
        var first = long.MaxValue;
        for (now = 300; now < 12_000 && first == long.MaxValue; now += 2)
        {
            if (meter.Record() == FloodVerdict.Flood)
            {
                first = now;
            }
        }

        // Second 0 goes over at 700 ms, so the flood is 10 s after that, not 10 s after the second began.
        Assert.Equal(10_700, first);
    }

    [Fact]
    public void S3_7_a_quiet_second_ends_the_run()
    {
        long now = 0;
        var meter = new FloodMeter(200, TimeSpan.FromSeconds(10), () => now);
        for (now = 0; now < 9_000; now += 2)
        {
            Assert.NotEqual(FloodVerdict.Flood, meter.Record());
        }

        // Second 9 stays within the rate, so the run over it starts again at second 10.
        for (now = 9_000; now < 10_000; now += 10)
        {
            Assert.Equal(FloodVerdict.Within, meter.Record());
        }

        for (now = 10_000; now < 19_000; now += 2)
        {
            Assert.NotEqual(FloodVerdict.Flood, meter.Record());
        }
    }
}
