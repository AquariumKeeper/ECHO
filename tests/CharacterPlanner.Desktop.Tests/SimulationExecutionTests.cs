using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.Tests;

public sealed class SimulationExecutionTests
{
    [Theory]
    [InlineData(SimulateViewModel.SwapWhenEmpty)]
    [InlineData(SimulateViewModel.SwapAfterKill)]
    public async Task DrivenWeaponSwapsRunWithIncomingFireButNotWithFixedAssumptions(string mode)
    {
        var dispatcher = new QueueDispatcher();
        var session = Session();
        Assert.True(session.Apply([new EquipmentOperation("secondary_weapon", "secondary_weapon_police_m4")], 0, EditOrigin.Desktop).Succeeded);
        using var form = Form(session, dispatcher);
        form.DriveBuffs = true;
        form.Swaps = mode;
        Assert.False(form.HasProblem);
        Assert.True(form.RunCommand.CanExecute(null));
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        Assert.NotNull(form.Result);
        form.Incoming = true;
        Assert.False(form.HasProblem);
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        Assert.NotNull(form.Result!.Incoming);
        // Whether the shield allows both weapons is the engine's check, made with the build.
        form.IncludeShield = true;
        Assert.False(form.HasProblem);
        form.IncludeShield = false;
        form.DriveBuffs = false;
        Assert.Equal(SimulateViewModel.SwapsUnderFire, form.Problem);
        Assert.False(form.RunCommand.CanExecute(null));
    }
    private static PlannerSession Session(bool weapon = true)
    {
        var build = BuildSnapshot.Create(Division.Pack);
        if (weapon) { build = build.WithEquipment("primary_weapon", "primary_weapon_acr"); }
        var state = new PlannerState(build, ImmutableDictionary<string, string>.Empty
            .Add(GamePack.WeaponSlotSelector, "primary_weapon").Add("enemy_target", "1"));
        return new(Division, state, new OperationApplier(Division));
    }

    private static SimulateViewModel Form(PlannerSession session, QueueDispatcher dispatcher) =>
        new(session, dispatcher) { FightLength = "0.25", Fights = "2" };

    [Fact]
    public async Task CoercedTimelineSelectionPublishesItsCanonicalValue()
    {
        var dispatcher = new QueueDispatcher();
        using var form = Form(Session(), dispatcher);
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        var notifications = new List<string?>();
        form.PropertyChanged += (_, change) => notifications.Add(change.PropertyName);
        form.SelectedTimelineIndex = -1;
        Assert.Equal(0, form.SelectedTimelineIndex);
        Assert.Contains(nameof(form.SelectedTimelineIndex), notifications);
    }

    [Fact]
    public void SimulationModelsRequireBothSessionAndDispatcher()
    {
        Assert.Throws<ArgumentNullException>(() => new SimulateViewModel(null!, new QueueDispatcher()));
        Assert.Throws<ArgumentNullException>(() => new SimulateViewModel(Session(), null!));
    }

    [Fact]
    public async Task RunRefreshesWeaponsBeforeAPendingExternalEditNotification()
    {
        var session = Session();
        Assert.True(session.Apply([new EquipmentOperation("secondary_weapon", "secondary_weapon_acr")], 0, EditOrigin.Desktop).Succeeded);
        var dispatcher = new QueueDispatcher();
        using var form = Form(session, dispatcher);
        form.DriveBuffs = false;
        form.Swaps = SimulateViewModel.SwapWhenEmpty;
        Assert.True(form.RunCommand.CanExecute(null));
        await Task.Run(() => Assert.True(session.Apply([new EquipmentOperation("secondary_weapon", "") { Clear = true }],
            session.Snapshot.Revision, EditOrigin.Session).Succeeded), TestContext.Current.CancellationToken);
        form.RunCommand.Execute(null);
        Assert.False(form.IsRunning);
        Assert.Empty(form.SwapWeapons);
        Assert.Equal("", form.SwapWeapon.Id);
        Assert.Contains("Equip and choose", form.Problem, StringComparison.Ordinal);
        dispatcher.RunPending();
        Assert.Empty(form.SwapWeapons);
    }

    [Fact]
    public async Task PendingWeaponRefreshDoesNotCancelARunAlreadyUsingTheNewRevision()
    {
        var session = Session();
        var dispatcher = new QueueDispatcher();
        using var form = Form(session, dispatcher);
        Assert.True(session.Apply([new EquipmentOperation("secondary_weapon", "secondary_weapon_acr")], 0, EditOrigin.Session).Succeeded);
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        Assert.NotNull(form.Result);
        Assert.Equal("secondary_weapon", Assert.Single(form.SwapWeapons).Id);
    }

    [Fact]
    public async Task NullSwapSelectionsPreserveResultsAndRunningRequests()
    {
        var dispatcher = new QueueDispatcher();
        using var form = Form(Session(), dispatcher);
        form.RunCommand.Execute(null);
        await form.Running;
        form.Swaps = null!;
        Assert.True(form.IsRunning);
        dispatcher.RunPending();
        var result = Assert.IsType<DummyFightResult>(form.Result);
        form.Swaps = null!;
        Assert.Same(result, form.Result);
        Assert.Equal(SimulateViewModel.NoSwaps, form.Swaps);
        Assert.False(form.HasProblem);
    }

    [Fact]
    public void IdleMetadataEditsDoNotQueueSimulationRefreshesOrNotifications()
    {
        var session = Session();
        var dispatcher = new QueueDispatcher();
        using var form = Form(session, dispatcher);
        var notifications = 0;
        form.PropertyChanged += (_, _) => notifications++;
        Assert.True(session.Apply([new NameOperation("Only metadata")], 0, EditOrigin.Session).Succeeded);
        Assert.Equal(0, dispatcher.PendingCount);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task BurstsOfEditsQueueOnlyOneInvalidationAndThenBecomeIdle()
    {
        var session = Session();
        var dispatcher = new QueueDispatcher();
        using var form = Form(session, dispatcher);
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        for (var index = 0; index < 10; index++)
        { Assert.True(session.Apply([new NameOperation($"Name {index}")], session.Snapshot.Revision, EditOrigin.Session).Succeeded); }
        Assert.Equal(1, dispatcher.PendingCount);
        dispatcher.RunPending();
        Assert.Null(form.Result);
        Assert.True(session.Apply([new NameOperation("Idle")], session.Snapshot.Revision, EditOrigin.Session).Succeeded);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1, -1, 0)]
    [InlineData(-1, 2, 0)]
    [InlineData(5, 2, 1)]
    [InlineData(1, 2, 0.5)]
    public void PresentationProgressAlwaysStaysFiniteAndBounded(int completed, int total, double expected) =>
        Assert.Equal(expected, SimulationPresentation.ProgressFraction(new(completed, total)));

    [Fact]
    public async Task NullableTotalDpsIsPresentedAsUnavailable()
    {
        var dispatcher = new QueueDispatcher();
        using var form = Form(Session(), dispatcher);
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        Assert.Contains("Total DPS: Unavailable", SimulationPresentation.Summary(form.Result! with { TotalDps = null }), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedEditorConstructorDoesNotAttachSimulationListeners()
    {
        var broken = Division with { Pack = Division.Pack with { Slots = [.. Division.Pack.Slots, Division.Pack.Slots[0] with { Name = "Conflicting duplicate" }] } };
        var session = new PlannerSession(broken, PlannerState.Of(BuildSnapshot.Create(Division.Pack)), new OperationApplier(Division));
        var dispatcher = new QueueDispatcher();
        Assert.Throws<ArgumentException>(() => new EditorViewModel(session, new CompareViewModelTests.FakeLibrary(), dispatcher));
        Assert.True(session.Apply([new NameOperation("After failure")], 0, EditOrigin.Session).Succeeded);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public void ADisposedEditorCannotCreateItsLazySimulationModel()
    {
        var editor = new EditorViewModel(Session(), new CompareViewModelTests.FakeLibrary(), new QueueDispatcher());
        editor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => editor.Simulate);
    }

    [Fact]
    public void SwapChoicesExcludeEmptyAndActiveWeaponSlots()
    {
        var session = Session();
        var dispatcher = new QueueDispatcher();
        using var form = Form(session, dispatcher);
        Assert.Empty(form.SwapWeapons);
        Assert.True(session.Apply([new EquipmentOperation("secondary_weapon", "secondary_weapon_acr")],
            session.Snapshot.Revision, EditOrigin.Desktop).Succeeded);
        dispatcher.RunPending();
        Assert.Equal("secondary_weapon", Assert.Single(form.SwapWeapons).Id);
        Assert.Equal("secondary_weapon", form.SwapWeapon.Id);
    }

    [Fact]
    public async Task RunsCapturedOptionsAndPublishesResultsOnlyOnTheDispatcher()
    {
        var dispatcher = new QueueDispatcher();
        using var form = Form(Session(), dispatcher);
        form.RunCommand.Execute(null);
        Assert.True(form.IsRunning);
        Assert.False(form.RunCommand.CanExecute(null));
        await form.Running;
        Assert.Null(form.Result);
        Assert.False(form.Distributions.HasRows);
        dispatcher.RunPending();
        var result = Assert.IsType<DummyFightResult>(form.Result);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(0.25, result.Duration);
        Assert.Equal(1UL, result.Seed);
        Assert.Same(result.WeaponDps, form.Distributions.Rows[0].Summary);
        Assert.Equal(1, form.Progress);
        Assert.False(form.IsRunning);
        Assert.Contains("Weapon DPS:", form.ResultText, StringComparison.Ordinal);
        Assert.NotEmpty(form.Limitations);
        Assert.True(form.RunCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("options")]
    [InlineData("edit")]
    [InlineData("dispose")]
    public async Task QueuedOldNotificationsCannotRestoreInvalidatedResults(string action)
    {
        var session = Session();
        var dispatcher = new QueueDispatcher();
        using var form = Form(session, dispatcher);
        form.Incoming = true;
        form.CaptureEvents = true;
        form.RunCommand.Execute(null);
        await form.Running;
        if (action == "cancel") { form.CancelCommand.Execute(null); }
        else if (action == "options") { form.Seed = "2"; }
        else if (action == "edit") { Assert.True(session.Apply([new NameOperation("Changed")], 0, EditOrigin.Desktop).Succeeded); }
        else { form.Dispose(); }
        dispatcher.RunPending();
        Assert.Null(form.Result);
        Assert.Empty(form.ResultText);
        Assert.False(form.Distributions.HasRows);
        Assert.Empty(form.Distributions.Note);
        Assert.False(form.IncomingReport.HasData);
        Assert.Empty(form.IncomingReport.Timings);
        Assert.False(form.UptimeReport.HasResult);
        Assert.Empty(form.UptimeReport.Sections);
        Assert.False(form.EventReport.HasCapture);
        Assert.Empty(form.EventReport.Rows);
        Assert.False(form.HasTimeline);
        Assert.Empty(form.TimelineDetails);
        Assert.False(form.IsRunning);
        Assert.Equal(0, form.Progress);
    }

    [Fact]
    public async Task EventCaptureCanBeToggledWithoutChangingSeededSummaries()
    {
        var dispatcher = new QueueDispatcher();
        using var form = Form(Session(), dispatcher);
        Assert.False(form.CaptureEvents);
        form.CaptureEvents = true;
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        var captured = Assert.IsType<DummyFightResult>(form.Result);
        Assert.NotEmpty(captured.FirstFightEvents);
        Assert.Equal(captured.FirstFightEvents.Length, form.EventReport.Rows.Count);
        form.CaptureEvents = false;
        Assert.Null(form.Result);
        Assert.False(form.EventReport.HasCapture);
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        var uncaptured = Assert.IsType<DummyFightResult>(form.Result);
        Assert.Empty(uncaptured.FirstFightEvents);
        Assert.False(form.EventReport.HasCapture);
        Assert.Equal(captured.WeaponDps, uncaptured.WeaponDps);
        Assert.Equal(captured.Timeline, uncaptured.Timeline);
        Assert.Equal(captured.Kills, uncaptured.Kills);
    }

    [Fact]
    public async Task NewOptionsCanRunBeforeOldQueuedResultsAreApplied()
    {
        var dispatcher = new QueueDispatcher();
        using var form = Form(Session(), dispatcher);
        form.RunCommand.Execute(null);
        await form.Running;
        form.Seed = "23";
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        Assert.Equal(23UL, form.Result!.Seed);
    }

    [Fact]
    public async Task AverageModeRunsOneFightWithExplicitFixedAssumptions()
    {
        var dispatcher = new QueueDispatcher();
        using var form = Form(Session(), dispatcher);
        form.DriveBuffs = false;
        form.AverageFight = true;
        Assert.False(form.HasProblem);
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        Assert.Equal(SimulationMode.Average, form.Result!.Mode);
        Assert.Equal(1, form.Result.Iterations);
        Assert.Contains("one deterministic fight", form.Distributions.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineFailuresAreShownWithCodesAndNoPartialResults()
    {
        var dispatcher = new QueueDispatcher();
        using var form = Form(Session(weapon: false), dispatcher);
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        Assert.True(form.HasProblem);
        Assert.Contains("weapon-required", form.Status, StringComparison.Ordinal);
        Assert.Null(form.Result);
        Assert.False(form.IsRunning);
        Assert.Null(form.Problem);
        Assert.NotNull(form.RunError);
        Assert.Equal(0, form.Progress);
        Assert.True(form.RunCommand.CanExecute(null));
        form.RunCommand.Execute(null);
        await form.Running;
        dispatcher.RunPending();
        Assert.Contains("weapon-required", form.RunError, StringComparison.Ordinal);
        Assert.True(form.RunCommand.CanExecute(null));
    }

    [Fact]
    public void UnsupportedCombinationsAndInvalidSeedsCannotRun()
    {
        var dispatcher = new QueueDispatcher();
        using var form = Form(Session(), dispatcher);
        form.Seed = "-1";
        Assert.False(form.RunCommand.CanExecute(null));
        form.Seed = "18446744073709551615";
        Assert.True(form.RunCommand.CanExecute(null));
        form.IncludeSkills = true;
        Assert.Contains("require incoming", form.Problem, StringComparison.Ordinal);
        form.Incoming = true;
        Assert.False(form.HasProblem);
        form.AverageFight = true;
        Assert.False(form.RunCommand.CanExecute(null));
    }
}
