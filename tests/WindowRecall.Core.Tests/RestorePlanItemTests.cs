using WindowRecall.Core;

namespace WindowRecall.Core.Tests;

public sealed class RestorePlanItemTests
{
    [Theory]
    [InlineData(RestoreAction.Ambiguous)]
    [InlineData(RestoreAction.Launch)]
    [InlineData(RestoreAction.Skip)]
    public void UnsafeOrExplicitActionsCannotAutoApply(RestoreAction action)
    {
        RestorePlanItem item = new("saved", null, action, null, null, "preview");
        Assert.False(item.CanAutoApply);
    }

    [Fact]
    public void OperationWithoutCurrentWindowCannotAutoApply()
    {
        RestorePlanItem item = new("saved", null, RestoreAction.MoveResize, new DesktopRect(0, 0, 100, 100), null, "missing window");
        Assert.False(item.CanAutoApply);
    }
}
