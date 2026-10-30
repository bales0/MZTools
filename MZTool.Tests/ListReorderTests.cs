namespace MZTools.Tests;

public class ListReorderTests
{
    [Fact]
    public void MoveItems_MovesSingleItemToEnd()
    {
        var items = new List<string> { "A", "B", "C", "D" };

        bool changed = ListReorder.MoveItems(items, new[] { 1 }, 4);

        Assert.True(changed);
        Assert.Equal(new[] { "A", "C", "D", "B" }, items);
    }

    [Fact]
    public void MoveItems_MovesMultipleItemsAndKeepsTheirOrder()
    {
        var items = new List<string> { "A", "B", "C", "D", "E" };

        bool changed = ListReorder.MoveItems(items, new[] { 3, 1 }, 5);

        Assert.True(changed);
        Assert.Equal(new[] { "A", "C", "E", "B", "D" }, items);
    }

    [Fact]
    public void MoveItems_DropInsideSelectionDoesNotChangeOrder()
    {
        var items = new List<string> { "A", "B", "C", "D" };

        bool changed = ListReorder.MoveItems(items, new[] { 1, 2 }, 2);

        Assert.False(changed);
        Assert.Equal(new[] { "A", "B", "C", "D" }, items);
    }

    [Fact]
    public void MoveItems_MovesMultipleItemsToStart()
    {
        var items = new List<string> { "A", "B", "C", "D" };

        bool changed = ListReorder.MoveItems(items, new[] { 1, 3 }, 0);

        Assert.True(changed);
        Assert.Equal(new[] { "B", "D", "A", "C" }, items);
    }
}
