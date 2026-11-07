global using static CharacterPlanner.Desktop.Tests.TestPack;
using System.Collections.Concurrent;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>
/// The Division pack the desktop app bundles (copied in through the project reference), read without its artwork,
/// which editing doesn't need; the helpers the tests share; and a test dispatcher.
/// </summary>
internal static class TestPack
{
    public static CompiledPackExpressions Division { get; } = Load();

    private static CompiledPackExpressions Load()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "packs", "division-2");
        var files = Directory.EnumerateFiles(directory).ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path), StringComparer.Ordinal);
        return PackValidator.Load(files).Compiled ?? throw new InvalidOperationException("The Division pack didn't load");
    }

    /// <summary>The editor's slot with this id.</summary>
    public static SlotViewModel Slot(this EditorViewModel editor, string id) => editor.SlotGroups.SelectMany(group => group.Slots).Single(slot => slot.Id == id);

    /// <summary>The choice with this id.</summary>
    public static Choice Pick(IEnumerable<Choice> choices, string id) => choices.Single(choice => choice.Id == id);

    /// <summary>Queues posted updates until the test runs them on its own thread, as the UI thread would.</summary>
    public sealed class QueueDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> pending = new();

        public int PendingCount => pending.Count;

        public void Post(Action action) => pending.Enqueue(action);

        public void RunPending()
        {
            while (pending.TryDequeue(out var action)) { action(); }
        }
    }
}
