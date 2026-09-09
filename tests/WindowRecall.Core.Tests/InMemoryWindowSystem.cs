using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Core.Tests;

/// <summary>Deterministic in-memory window system for Core tests. Mirrors what a real adapter does
/// (capture current desktop, apply approved items, track mutated positions) without native handles.</summary>
internal sealed class InMemoryWindowSystem : IWindowSystem
{
    private CurrentDesktop desktop;
    private readonly WindowSystemCapabilities capabilities;

    public InMemoryWindowSystem(CurrentDesktop desktop, WindowSystemCapabilities? capabilities = null)
    {
        this.desktop = desktop;
        this.capabilities = capabilities ?? new WindowSystemCapabilities(true, true, true, false, null);
    }

    public int ApplyCalls { get; private set; }
    public Func<RestorePlanItem, WindowOutcome>? OutcomeOverride { get; set; }

    public Task<CurrentDesktop> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(desktop);
    }

    public Task<ImmutableArray<WindowOutcome>> ApplyAsync(RestorePlan plan, ImmutableArray<string> approvedSavedWindowIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        ApplyCalls++;
        ImmutableArray<WindowOutcome>.Builder outcomes = ImmutableArray.CreateBuilder<WindowOutcome>();
        foreach (RestorePlanItem item in plan.Items)
        {
            WindowOutcome? overridden = OutcomeOverride?.Invoke(item);
            if (overridden is not null)
            {
                outcomes.Add(overridden);
                continue;
            }
            if (!item.IsIncluded)
            {
                outcomes.Add(new(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Skipped, "Not selected."));
                continue;
            }
            int index = -1;
            for (int windowIndex = 0; windowIndex < desktop.Windows.Length; windowIndex++)
            {
                if (desktop.Windows[windowIndex].WindowId == item.CurrentWindowId)
                {
                    index = windowIndex;
                    break;
                }
            }
            if (index < 0)
            {
                outcomes.Add(new(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.NotFound, "Window disappeared."));
                continue;
            }
            WindowSnapshot window = desktop.Windows[index];
            desktop = desktop with
            {
                Windows = desktop.Windows.SetItem(index, window with
                {
                    NormalBounds = item.TargetBounds ?? window.NormalBounds,
                    State = item.TargetState ?? window.State,
                }),
            };
            outcomes.Add(new(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Succeeded, "Applied."));
        }
        return Task.FromResult(outcomes.ToImmutable());
    }

    public Task<WindowSystemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(capabilities);
    }
}
