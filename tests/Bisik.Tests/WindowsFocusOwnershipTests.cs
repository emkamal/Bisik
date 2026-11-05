using Bisik.Core;
using FluentAssertions;

namespace Bisik.Tests;

public sealed class WindowsFocusOwnershipTests
{
    [Fact]
    public void Embedded_renderer_should_require_native_focus_and_accessibility_ancestry_in_the_foreground()
    {
        var foreground = new IntPtr(100);
        var focus = new IntPtr(101);
        var embedded = new IntPtr(102);
        var roots = new Dictionary<IntPtr, IntPtr> { [focus] = foreground, [embedded] = foreground };

        WindowsFocusOwnership.AllowsTarget(false, foreground, focus,
            [IntPtr.Zero, IntPtr.Zero, embedded], window => roots[window]).Should().BeTrue();
    }

    [Fact]
    public void Renderer_from_another_window_must_not_be_accepted_even_with_native_focus_in_foreground()
    {
        var foreground = new IntPtr(100);
        var focus = new IntPtr(101);

        WindowsFocusOwnership.AllowsTarget(false, foreground, focus, [new IntPtr(201)],
            window => window == focus ? foreground : new IntPtr(200)).Should().BeFalse();
    }

    [Fact]
    public void Matching_accessibility_ancestry_must_not_override_native_focus_in_another_window()
    {
        var foreground = new IntPtr(100);
        var focus = new IntPtr(201);

        WindowsFocusOwnership.AllowsTarget(false, foreground, focus, [new IntPtr(101)],
            window => window == focus ? new IntPtr(200) : foreground).Should().BeFalse();
    }

    [Fact]
    public void Missing_native_ancestry_must_not_grant_a_cross_process_target()
    {
        var foreground = new IntPtr(100);

        WindowsFocusOwnership.AllowsTarget(false, foreground, new IntPtr(101),
            [IntPtr.Zero], _ => foreground).Should().BeFalse();
    }

    [Fact]
    public void Ancestor_lookup_should_remain_bounded()
    {
        var foreground = new IntPtr(100);
        var handles = Enumerable.Repeat(IntPtr.Zero, 40).Append(new IntPtr(101));

        WindowsFocusOwnership.AllowsTarget(false, foreground, new IntPtr(101), handles,
            _ => foreground).Should().BeFalse();
    }

    [Fact]
    public void Existing_same_process_targets_should_not_need_cross_process_ancestry()
    {
        WindowsFocusOwnership.AllowsTarget(true, new IntPtr(100), new IntPtr(101), [],
            _ => throw new InvalidOperationException("Unexpected native lookup.")).Should().BeTrue();
    }
}
