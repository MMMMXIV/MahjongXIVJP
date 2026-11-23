using MahjongXiv.Core.Game;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class TileButtonResolverTests
{
    private struct Tree : ITileNodeAccess
    {
        public bool ContainerVisible = true;
        public bool ChildVisible = true;
        public bool ChildExists = true;
        public bool ChildIsButton = true;
        public bool ChildEnabled = true;
        public bool ChildHasOwner = true;
        public nint ValidatedNode;
        public Tree() { }
        public bool IsVisible(nint node) => node == 100 ? ContainerVisible : node == 200 && ChildVisible;
        public nint ChildAt(nint node, int index) => node == 100 && index == 0 && ChildExists ? 200 : 0;
        public bool IsEnabledButton(nint node, out string reason)
        {
            ValidatedNode = node;
            // Parent 100 is deliberately NOT a button: this is the 0.5.3.1 regression fixture.
            bool valid = node == 200 && ChildIsButton && ChildEnabled && ChildHasOwner;
            reason = valid ? "" : "child is not an enabled button with an owner";
            return valid;
        }
    }

    [Fact]
    public void NonButtonContainerResolvesItsEnabledChildButton()
    {
        var tree = new Tree();
        Assert.Equal((nint)200, TileButtonResolver.Find(100, 0, ref tree, out string reason));
        Assert.Equal((nint)200, tree.ValidatedNode);
        Assert.Equal("", reason);
    }

    [Theory]
    [InlineData("hidden-container")]
    [InlineData("missing-child")]
    [InlineData("hidden-child")]
    [InlineData("non-button-child")]
    [InlineData("disabled-child")]
    [InlineData("missing-owner")]
    public void InvalidChildNeverFallsBackToTheParentOrAnotherNode(string condition)
    {
        var tree = new Tree
        {
            ContainerVisible = condition != "hidden-container",
            ChildExists = condition != "missing-child",
            ChildVisible = condition != "hidden-child",
            ChildIsButton = condition != "non-button-child",
            ChildEnabled = condition != "disabled-child",
            ChildHasOwner = condition != "missing-owner",
        };
        Assert.Equal((nint)0, TileButtonResolver.Find(100, 0, ref tree, out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void MissingContainerStopsBeforeReadingChildren()
    {
        var tree = new Tree();
        Assert.Equal((nint)0, TileButtonResolver.Find(0, 0, ref tree, out _));
        Assert.Equal((nint)0, tree.ValidatedNode);
    }
}
