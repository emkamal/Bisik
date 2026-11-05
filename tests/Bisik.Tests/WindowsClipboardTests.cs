using Bisik.Core;
using FluentAssertions;

namespace Bisik.Tests;

public sealed class WindowsClipboardTests
{
    [Fact]
    public void Write_receipt_should_use_the_revision_published_when_the_clipboard_closes()
    {
        var access = new FakeOwnershipAccess();
        access.AfterFirstClose = () => access.Revision++;

        var receipt = WindowsClipboard.CompleteWrite(access.ClipboardOwner, access);

        receipt.Should().Be(42);
        access.IsOpen.Should().BeFalse();
        access.CloseCount.Should().Be(2);
        access.SequenceReadCount.Should().Be(1);
    }

    [Fact]
    public void Write_receipt_should_not_adopt_an_intervening_copy_from_another_owner()
    {
        var access = new FakeOwnershipAccess();
        var bisikOwner = access.ClipboardOwner;
        access.AfterFirstClose = () =>
        {
            access.ClipboardOwner = new IntPtr(200);
            access.Revision += 3;
        };

        WindowsClipboard.CompleteWrite(bisikOwner, access).Should().BeNull();

        access.SequenceReadCount.Should().Be(0);
        access.IsOpen.Should().BeFalse();
        access.CloseCount.Should().Be(2);
    }

    [Fact]
    public void Write_receipt_should_fail_without_adopting_a_revision_when_the_clipboard_is_busy()
    {
        var access = new FakeOwnershipAccess { CanOpen = false };

        WindowsClipboard.CompleteWrite(access.ClipboardOwner, access).Should().BeNull();

        access.SequenceReadCount.Should().Be(0);
        access.CloseCount.Should().Be(1);
        access.IsOpen.Should().BeFalse();
    }

    private sealed class FakeOwnershipAccess : IClipboardOwnershipAccess
    {
        public IntPtr ClipboardOwner { get; set; } = new(100);
        public uint Revision { get; set; } = 41;
        public bool IsOpen { get; private set; } = true;
        public bool CanOpen { get; init; } = true;
        public int CloseCount { get; private set; }
        public int SequenceReadCount { get; private set; }
        public Action? AfterFirstClose { get; set; }
        public IntPtr Owner => IsOpen ? ClipboardOwner : throw new InvalidOperationException("No clipboard lock.");
        public uint Sequence
        {
            get
            {
                if (!IsOpen) throw new InvalidOperationException("No clipboard lock.");
                SequenceReadCount++;
                return Revision;
            }
        }

        public bool Open(IntPtr owner)
        {
            IsOpen.Should().BeFalse();
            IsOpen = CanOpen;
            return IsOpen;
        }

        public bool Close()
        {
            IsOpen.Should().BeTrue();
            IsOpen = false;
            if (++CloseCount == 1) AfterFirstClose?.Invoke();
            return true;
        }
    }
}
