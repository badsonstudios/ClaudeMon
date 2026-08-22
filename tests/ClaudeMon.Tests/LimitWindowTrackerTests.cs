namespace ClaudeMon.Tests;

using System.Text.Json;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;

/// <summary>
/// The correlated limit log's window state machine (issue #184), driven entirely with
/// synthetic samples: no clock, no disk. Every boundary rule the ticket cares about — peak/last
/// rollup, server-authoritative rollover, jitter tolerance, idle expiry, delta clamping,
/// cross-restart continuation, missed-window catch-up, plan stamping — is pinned here.
/// </summary>
public class LimitWindowTrackerTests
{
    // A fixed anchor keeps every test deterministic; nothing here reads a real clock.
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(5);

    // A session window that began two minutes before T0: its start (resets − 5h) falls just
    // inside the observation chain a state seeded with Observed(T0 − Poll) establishes, so a
    // window opened at T0 counts as covered — the shape of a window seen from its birth.
    private static readonly DateTimeOffset BornResets =
        T0 + UsageWindows.FiveHour - TimeSpan.FromMinutes(2);

    private static LimitLogState Fresh => new() { Version = LimitLogState.CurrentVersion };

    private static UsageLimit Limit(
        string kind, double? pct, DateTimeOffset? resets,
        string? group = null, string? model = null, string? severity = null, bool? active = null) =>
        new(kind, group, pct, severity, resets, active,
            model is null ? null : new LimitScope(new LimitScopeModel(model)));

    private static UsageResponse Usage(params UsageLimit[] limits) => new(null, null, limits);

    private static Dictionary<string, ModelTokens> Tok(params (string Model, long In)[] models) =>
        models.ToDictionary(
            m => m.Model, m => new ModelTokens(m.In, 0, 0, 0), StringComparer.OrdinalIgnoreCase);

    // A state that has already observed once at `at` (baseline + observation chain), so tests
    // exercise steady-state behavior rather than the flagged first-run baseline.
    private static LimitLogState Observed(DateTimeOffset at, Dictionary<string, ModelTokens>? tok = null) =>
        LimitWindowTracker.Observe(Fresh, at, Usage(), tok ?? Tok(("opus", 0)), plan: null).NewState;

    // --- Samples ---

    [Fact]
    public void Observe_EmitsSampleWithEveryLimitFieldVerbatimAndCumulativeTokens()
    {
        var resets = T0 + TimeSpan.FromHours(3);
        var result = LimitWindowTracker.Observe(
            Fresh, T0,
            Usage(Limit("weekly_scoped", 41.5, resets, group: "weekly", model: "Opus 4",
                severity: "warning", active: true)),
            Tok(("claude-opus-4-6", 123)), ClaudePlan.Max20x);

        var snap = Assert.Single(result.Sample.Limits);
        Assert.Equal("weekly_scoped", snap.Kind);
        Assert.Equal("weekly", snap.Group);
        Assert.Equal(41.5, snap.Percent);
        Assert.Equal("warning", snap.Severity);
        Assert.Equal(resets, snap.ResetsAt);
        Assert.True(snap.IsActive);
        Assert.Equal("Opus 4", snap.ScopeModel);
        Assert.Equal(T0, result.Sample.Timestamp);
        Assert.Equal(123, result.Sample.TokensByModel!["claude-opus-4-6"].InputTokens);
        Assert.Equal(LimitLogSchema.SchemaVersion, result.Sample.Version);
    }

    [Fact]
    public void Observe_LegacyPayloadWithoutLimits_SynthesizesTheCanonicalPair()
    {
        var usage = new UsageResponse(
            new UsageBucket(42, T0 + TimeSpan.FromHours(2)),
            new UsageBucket(17, T0 + TimeSpan.FromDays(3)));

        var result = LimitWindowTracker.Observe(Fresh, T0, usage, Tok(("opus", 0)), null);

        Assert.Collection(result.Sample.Limits,
            s => { Assert.Equal("session", s.Kind); Assert.Equal(42, s.Percent); },
            s => { Assert.Equal("weekly_all", s.Kind); Assert.Equal(17, s.Percent); });
        // The synthesized entries still open windows — tracking works on old payloads too.
        Assert.Equal(2, result.NewState.Windows.Count);
    }

    [Fact]
    public void Observe_ScannerUnavailable_LogsNullTokensAndNothingThrows()
    {
        var result = LimitWindowTracker.Observe(
            Fresh, T0, Usage(Limit("session", 10, T0 + TimeSpan.FromHours(5))),
            cumulativeTokens: null, plan: null);

        Assert.Null(result.Sample.TokensByModel);
        var window = Assert.Single(result.NewState.Windows);
        Assert.Empty(window.TokensByModel);
    }

    // --- Opening windows ---

    [Fact]
    public void Observe_OpensOneWindowPerLimit_StartDerivedFromKnownKind()
    {
        var sessionResets = T0 + TimeSpan.FromHours(2);
        var weeklyResets = T0 + TimeSpan.FromDays(3);
        var result = LimitWindowTracker.Observe(
            Observed(T0 - Poll), T0,
            Usage(Limit("session", 30, sessionResets), Limit("weekly_all", 12, weeklyResets)),
            Tok(("opus", 100)), null);

        Assert.Empty(result.Finalized);
        Assert.Equal(2, result.NewState.Windows.Count);
        var session = result.NewState.Windows.Single(w => w.Kind == "session");
        Assert.Equal(sessionResets - UsageWindows.FiveHour, session.Start);
        Assert.False(session.StartApprox);
        var weekly = result.NewState.Windows.Single(w => w.Kind == "weekly_all");
        Assert.Equal(weeklyResets - UsageWindows.SevenDay, weekly.Start);
    }

    [Fact]
    public void Observe_UnknownKind_TracksWithApproximateStart()
    {
        var result = LimitWindowTracker.Observe(
            Observed(T0 - Poll), T0,
            Usage(Limit("monthly_special", 5, T0 + TimeSpan.FromDays(20))),
            Tok(("opus", 0)), null);

        var window = Assert.Single(result.NewState.Windows);
        Assert.Equal(T0, window.Start);
        Assert.True(window.StartApprox);
    }

    [Fact]
    public void Observe_FirstObservationEver_IsABaseline_WindowFlaggedIncomplete()
    {
        // With no previous sample there is no way to vouch for the burn since the window
        // started — the window is tracked, but flagged rather than presented as exact, and the
        // cumulative totals become a baseline instead of a burst.
        var result = LimitWindowTracker.Observe(
            Fresh, T0, Usage(Limit("session", 30, T0 + TimeSpan.FromHours(2))),
            Tok(("opus", 5000)), null);

        var window = Assert.Single(result.NewState.Windows);
        Assert.True(window.Incomplete);
        Assert.Equal(LimitWindowRecord.ReasonGapSpannedBoundary, window.IncompleteReason);
        Assert.Empty(window.TokensByModel);
        Assert.Equal(5000, result.NewState.LastTokens!["opus"].InputTokens);
    }

    [Fact]
    public void Observe_WindowAlreadyInFlightWhenObservationStarted_IsFlaggedIncomplete()
    {
        // The app has been polling for five minutes, but this window started an hour ago:
        // the burn before observation began is unknowable, so the record is flagged rather
        // than presented as exact.
        var result = LimitWindowTracker.Observe(
            Observed(T0 - Poll), T0,
            Usage(Limit("session", 30, T0 + TimeSpan.FromHours(4))),
            Tok(("opus", 0)), null);

        var window = Assert.Single(result.NewState.Windows);
        Assert.True(window.Incomplete);
        Assert.Equal(LimitWindowRecord.ReasonGapSpannedBoundary, window.IncompleteReason);
    }

    [Fact]
    public void Observe_WindowSeenFromItsBirth_IsComplete()
    {
        var result = LimitWindowTracker.Observe(
            Observed(T0 - Poll), T0,
            Usage(Limit("session", 2, BornResets)), Tok(("opus", 0)), null);

        var window = Assert.Single(result.NewState.Windows);
        Assert.False(window.Incomplete);
        Assert.Equal(BornResets - UsageWindows.FiveHour, window.Start);
    }

    [Fact]
    public void Observe_ScopedLimitsGetIndependentWindows_AndExactDuplicatesDedup()
    {
        var resets = T0 + TimeSpan.FromDays(2);
        var result = LimitWindowTracker.Observe(
            Observed(T0 - Poll), T0,
            Usage(
                Limit("weekly_scoped", 60, resets, model: "Opus 4"),
                Limit("weekly_scoped", 20, resets, model: "Fable"),
                // Exact (kind, scope) repeat: tracking keeps the higher percent, like the flyout.
                Limit("weekly_scoped", 45, resets, model: "Fable")),
            Tok(("opus", 0)), null);

        Assert.Equal(2, result.NewState.Windows.Count);
        Assert.Equal(60, result.NewState.Windows.Single(w => w.ScopeModel == "Opus 4").PeakPercent);
        Assert.Equal(45, result.NewState.Windows.Single(w => w.ScopeModel == "Fable").PeakPercent);
        // The sample still carries the payload verbatim, duplicates included.
        Assert.Equal(3, result.Sample.Limits.Count);
    }

    // --- Rollup within a window ---

    [Fact]
    public void Observe_TracksPeakAndLastPercentAcrossPolls()
    {
        var state = Observed(T0 - Poll);
        foreach (var (pct, at) in new[] { (10.0, T0), (80.0, T0 + Poll), (60.0, T0 + Poll + Poll) })
            state = LimitWindowTracker.Observe(
                state, at, Usage(Limit("session", pct, BornResets)), Tok(("opus", 0)), null).NewState;

        var window = Assert.Single(state.Windows);
        Assert.Equal(80, window.PeakPercent);
        Assert.Equal(60, window.LastPercent);
        Assert.Equal(3, window.SampleCount);
        Assert.Equal(T0 + Poll + Poll, window.LastSeenAt);
    }

    [Fact]
    public void Observe_AccumulatesTokenDeltasPerModelIntoTheActiveWindow()
    {
        var state = Observed(T0 - Poll, Tok(("opus", 100), ("fable", 10)));
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 10, BornResets)),
            Tok(("opus", 150), ("fable", 10)), null).NewState;
        state = LimitWindowTracker.Observe(
            state, T0 + Poll, Usage(Limit("session", 20, BornResets)),
            Tok(("opus", 175), ("fable", 40)), null).NewState;

        var window = Assert.Single(state.Windows);
        Assert.Equal(75, window.TokensByModel["opus"].InputTokens);
        Assert.Equal(30, window.TokensByModel["fable"].InputTokens);
    }

    [Fact]
    public void Observe_CumulativeDrop_ClampsToZeroBurn()
    {
        // The scanner's totals dip when its retention window prunes old days; that must read
        // as "no new burn", never negative burn.
        var state = Observed(T0 - Poll, Tok(("opus", 1000)));
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 10, BornResets)), Tok(("opus", 400)), null).NewState;

        var window = Assert.Single(state.Windows);
        Assert.Empty(window.TokensByModel);
    }

    [Fact]
    public void Observe_ScannerOutage_KeepsTheBaselineSoTheDeltaResumesAcrossIt()
    {
        var state = Observed(T0 - Poll, Tok(("opus", 100)));
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 10, BornResets)),
            cumulativeTokens: null, plan: null).NewState;
        state = LimitWindowTracker.Observe(
            state, T0 + Poll, Usage(Limit("session", 12, BornResets)),
            Tok(("opus", 180)), null).NewState;

        // The 80 burned across the outage lands when totals come back, measured from the last
        // known baseline — not rebased to zero.
        var window = Assert.Single(state.Windows);
        Assert.Equal(80, window.TokensByModel["opus"].InputTokens);
    }

    [Fact]
    public void Observe_ResetJitterWithinTolerance_IsTheSameWindow()
    {
        var state = Observed(T0 - Poll);
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 10, BornResets)), Tok(("opus", 0)), null).NewState;

        var result = LimitWindowTracker.Observe(
            state, T0 + Poll,
            Usage(Limit("session", 12, BornResets + TimeSpan.FromSeconds(30))),
            Tok(("opus", 0)), null);

        Assert.Empty(result.Finalized);
        var window = Assert.Single(result.NewState.Windows);
        Assert.Equal(2, window.SampleCount);
    }

    // --- Rollover ---

    [Fact]
    public void Observe_ResetsAtMoves_FinalizesTheOldWindowAndOpensTheNext()
    {
        // A session window observed from birth to within two minutes of its end, then the next
        // poll reports the successor window (opened one minute after the old one reset).
        var state = Observed(T0 - Poll, Tok(("opus", 100)));
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 85, BornResets)), Tok(("opus", 100)), null).NewState;
        state = LimitWindowTracker.Observe(
            state, BornResets - TimeSpan.FromMinutes(2),
            Usage(Limit("session", 92, BornResets)), Tok(("opus", 100)), null).NewState;

        var newResets = BornResets + TimeSpan.FromMinutes(1) + UsageWindows.FiveHour;
        var result = LimitWindowTracker.Observe(
            state, BornResets + TimeSpan.FromMinutes(3),
            Usage(Limit("session", 3, newResets)), Tok(("opus", 150)), null);

        var record = Assert.Single(result.Finalized);
        Assert.Equal(BornResets, record.End);
        Assert.Equal(BornResets - UsageWindows.FiveHour, record.Start);
        Assert.Equal(92, record.PeakPercent);
        Assert.Equal(92, record.LastPercent);
        Assert.False(record.Incomplete);
        // The straddling delta belongs to the new window (deterministic; error ≤ one poll).
        Assert.Empty(record.TokensByModel);

        var opened = Assert.Single(result.NewState.Windows);
        Assert.Equal(newResets - UsageWindows.FiveHour, opened.Start);
        Assert.Equal(50, opened.TokensByModel["opus"].InputTokens);
        Assert.False(opened.Incomplete);
    }

    [Fact]
    public void Observe_IdleExpiry_FinalizesExactlyOnceAndOpensNothing()
    {
        // The API echoes the old resets_at until new usage opens a window: one finalized
        // record when it passes, then nothing until a future resets_at appears.
        var state = Observed(T0 - Poll);
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 55, BornResets)), Tok(("opus", 0)), null).NewState;
        state = LimitWindowTracker.Observe(
            state, BornResets - TimeSpan.FromMinutes(2),
            Usage(Limit("session", 55, BornResets)), Tok(("opus", 0)), null).NewState;

        var expired = LimitWindowTracker.Observe(
            state, BornResets + TimeSpan.FromMinutes(3),
            Usage(Limit("session", 55, BornResets)), Tok(("opus", 0)), null);
        var record = Assert.Single(expired.Finalized);
        Assert.Equal(BornResets, record.End);
        Assert.False(record.Incomplete);
        Assert.Empty(expired.NewState.Windows);

        var echoed = LimitWindowTracker.Observe(
            expired.NewState, BornResets + TimeSpan.FromMinutes(8),
            Usage(Limit("session", 55, BornResets)), Tok(("opus", 0)), null);
        Assert.Empty(echoed.Finalized);
        Assert.Empty(echoed.NewState.Windows);
    }

    [Fact]
    public void Observe_LimitVanishingFromThePayload_KeepsItsWindowUntilTheResetPasses()
    {
        var resets = T0 + Poll + TimeSpan.FromMinutes(2);
        var state = Observed(T0 - Poll);
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("weekly_scoped", 70, resets, model: "Opus 4")),
            Tok(("opus", 0)), null).NewState;

        // Key absent this poll, reset still in the future: the window survives, and the burn
        // keeps accruing to it — the limit not being reported doesn't stop usage counting.
        var kept = LimitWindowTracker.Observe(
            state, T0 + Poll, Usage(Limit("session", 10, T0 + TimeSpan.FromHours(4))),
            Tok(("opus", 40)), null);
        var carried = kept.NewState.Windows.Single(w => w.ScopeModel == "Opus 4");
        Assert.Equal(40, carried.TokensByModel["opus"].InputTokens);

        // Still absent once the reset passes: the sweep finalizes it.
        var swept = LimitWindowTracker.Observe(
            kept.NewState, T0 + Poll + Poll,
            Usage(Limit("session", 12, T0 + TimeSpan.FromHours(4))), Tok(("opus", 40)), null);
        var record = Assert.Single(swept.Finalized);
        Assert.Equal("Opus 4", record.ScopeModel);
        Assert.DoesNotContain(swept.NewState.Windows, w => w.ScopeModel == "Opus 4");
    }

    // --- Restarts and gaps ---

    [Fact]
    public void Observe_StateRoundTripsThroughJson_AndContinuationIncludesOfflineBurn()
    {
        var state = Observed(T0 - Poll, Tok(("opus", 100)));
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 40, BornResets)), Tok(("opus", 100)), null).NewState;

        // Restart: the state survives serialization (what LimitLogStore persists).
        var reloaded = JsonSerializer.Deserialize<LimitLogState>(JsonSerializer.Serialize(state))!;

        // Two hours later the same window is still running. The window spans the whole gap, so
        // the offline burn (back-filled by the scanner from the transcripts) belongs to it.
        var result = LimitWindowTracker.Observe(
            reloaded, T0 + TimeSpan.FromHours(2),
            Usage(Limit("session", 70, BornResets)), Tok(("opus", 600)), null);

        Assert.Empty(result.Finalized);
        var window = Assert.Single(result.NewState.Windows);
        Assert.Equal(500, window.TokensByModel["opus"].InputTokens);
        Assert.Equal(70, window.PeakPercent);
        Assert.False(window.Incomplete);
    }

    [Fact]
    public void FinalizeExpired_MissedWindow_IsFlaggedOfflineAtWindowEnd()
    {
        var state = Observed(T0 - Poll);
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 65, BornResets)), Tok(("opus", 0)), null).NewState;

        // The app was closed when the window ended; next launch is hours later.
        var (finalized, newState) = LimitWindowTracker.FinalizeExpired(
            state, BornResets + TimeSpan.FromHours(2), ClaudePlan.Max20x);

        var record = Assert.Single(finalized);
        Assert.Equal(BornResets, record.End);
        Assert.True(record.Incomplete);
        Assert.Equal(LimitWindowRecord.ReasonOfflineAtWindowEnd, record.IncompleteReason);
        Assert.Equal(65, record.LastPercent);
        Assert.Equal(ClaudePlan.Max20x, record.Plan);
        Assert.Empty(newState.Windows);
    }

    [Fact]
    public void FinalizeExpired_LeavesStillRunningWindowsAlone()
    {
        var state = Observed(T0 - Poll);
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("weekly_all", 20, T0 + TimeSpan.FromDays(5))),
            Tok(("opus", 0)), null).NewState;

        var (finalized, newState) = LimitWindowTracker.FinalizeExpired(
            state, T0 + TimeSpan.FromHours(6), null);

        Assert.Empty(finalized);
        Assert.Single(newState.Windows);
    }

    [Fact]
    public void Observe_WindowOpenedAcrossAGap_ExcludesTheAmbiguousDeltaAndIsFlagged()
    {
        // A window is already in flight when observation resumes after three offline hours.
        // The cross-gap delta spans the old window's boundary, so attributing it to the new
        // window would be a guess — it is excluded and the window flagged instead.
        var state = Observed(T0, Tok(("opus", 100)));

        var relaunch = T0 + TimeSpan.FromHours(3);
        var result = LimitWindowTracker.Observe(
            state, relaunch,
            Usage(Limit("session", 25, relaunch + TimeSpan.FromHours(4))),
            Tok(("opus", 900)), null);

        var window = Assert.Single(result.NewState.Windows);
        Assert.True(window.Incomplete);
        Assert.Equal(LimitWindowRecord.ReasonGapSpannedBoundary, window.IncompleteReason);
        Assert.Empty(window.TokensByModel);
        // The baseline still advances — later deltas are measured from here.
        Assert.Equal(900, result.NewState.LastTokens!["opus"].InputTokens);
    }

    [Fact]
    public void Observe_RolloverAfterASleepGap_FlagsTheWindowThatEndedInTheGap()
    {
        // The machine slept across a session boundary without the app ever shutting down:
        // Observe (not the startup catch-up) meets the ended window, and the same staleness
        // rule flags it.
        var state = Observed(T0 - Poll);
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 65, BornResets)), Tok(("opus", 0)), null).NewState;

        var wake = BornResets + TimeSpan.FromHours(3);
        var result = LimitWindowTracker.Observe(
            state, wake, Usage(Limit("session", 10, wake + TimeSpan.FromHours(4))),
            Tok(("opus", 0)), null);

        var record = Assert.Single(result.Finalized);
        Assert.True(record.Incomplete);
        Assert.Equal(LimitWindowRecord.ReasonOfflineAtWindowEnd, record.IncompleteReason);
        var opened = Assert.Single(result.NewState.Windows);
        Assert.True(opened.Incomplete);
    }

    // --- Plan stamping ---

    [Fact]
    public void Observe_StampsThePlanAtBothEnds_AndFlagsAMidWindowChange()
    {
        var state = Observed(T0 - Poll);
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 50, BornResets)),
            Tok(("opus", 0)), ClaudePlan.Pro).NewState;

        // The plan changes mid-window; the idle expiry then finalizes it.
        var result = LimitWindowTracker.Observe(
            state, BornResets + TimeSpan.FromMinutes(3),
            Usage(Limit("session", 50, BornResets)), Tok(("opus", 0)), ClaudePlan.Max20x);

        var record = Assert.Single(result.Finalized);
        Assert.Equal(ClaudePlan.Pro, record.PlanAtStart);
        Assert.Equal(ClaudePlan.Max20x, record.Plan);
        Assert.True(record.PlanChanged);
    }

    [Fact]
    public void Observe_UnchangedPlan_IsNotFlaggedAsChanged()
    {
        var state = Observed(T0 - Poll);
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 50, BornResets)),
            Tok(("opus", 0)), ClaudePlan.Max5x).NewState;

        var result = LimitWindowTracker.Observe(
            state, BornResets + TimeSpan.FromMinutes(3),
            Usage(Limit("session", 50, BornResets)), Tok(("opus", 0)), ClaudePlan.Max5x);

        var record = Assert.Single(result.Finalized);
        Assert.Equal(ClaudePlan.Max5x, record.Plan);
        Assert.Equal(ClaudePlan.Max5x, record.PlanAtStart);
        Assert.False(record.PlanChanged);
    }

    [Fact]
    public void Observe_UnsetPlan_RecordsAsNull()
    {
        var state = Observed(T0 - Poll);
        state = LimitWindowTracker.Observe(
            state, T0, Usage(Limit("session", 50, BornResets)), Tok(("opus", 0)), null).NewState;

        var result = LimitWindowTracker.Observe(
            state, BornResets + TimeSpan.FromMinutes(3),
            Usage(Limit("session", 50, BornResets)), Tok(("opus", 0)), null);

        var record = Assert.Single(result.Finalized);
        Assert.Null(record.Plan);
        Assert.Null(record.PlanAtStart);
        Assert.False(record.PlanChanged);
    }
}
