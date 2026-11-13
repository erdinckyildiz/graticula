using System;
using System.Linq;
using Graticula.Features;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// An attachment table's change log read as net changes (ADR-289): the first entry says whether the attachment existed at
/// the start of the window, the last whether it exists at the end — and nothing is guessed from times.
/// </summary>
public sealed class AttachmentChangeLogTests
{
    private static AttachmentLogEntry Entry(string kind, int id, long feature = 1, Guid? global = null) => new(kind, id, feature, global);

    [Fact]
    public void An_attachment_first_seen_being_inserted_and_still_there_is_an_insert()
    {
        Guid gid = Guid.NewGuid();

        AttachmentChange change = Assert.Single(AttachmentChangeLog.Net(
            [Entry("insert", 5, 7, gid), Entry("update", 5, 7, gid)]));

        Assert.Equal(new AttachmentChange(AttachmentChangeKind.Insert, 5, 7, gid), change);
    }

    [Fact]
    public void An_attachment_first_seen_being_written_and_still_there_is_one_update_however_often_it_was_written()
    {
        AttachmentChange change = Assert.Single(AttachmentChangeLog.Net(
            [Entry("update", 5), Entry("update", 5), Entry("update", 5)]));

        Assert.Equal(AttachmentChangeKind.Update, change.Kind);
    }

    [Fact]
    public void An_attachment_that_was_there_and_is_not_is_a_delete_even_if_it_was_written_first()
    {
        AttachmentChange change = Assert.Single(AttachmentChangeLog.Net([Entry("update", 5), Entry("delete", 5)]));

        Assert.Equal(AttachmentChangeKind.Delete, change.Kind);
    }

    [Fact]
    public void An_attachment_added_and_removed_inside_the_window_reached_nobody_and_is_no_change()
    {
        Assert.Empty(AttachmentChangeLog.Net([Entry("insert", 5), Entry("update", 5), Entry("delete", 5)]));
    }

    [Fact]
    public void Each_attachment_is_judged_by_its_own_entries_and_the_answer_is_in_id_order()
    {
        Guid gid = Guid.NewGuid();

        AttachmentChange[] net =
        [
            .. AttachmentChangeLog.Net(
            [
                Entry("insert", 9),
                Entry("delete", 3),
                Entry("update", 6, 2, null),
                Entry("update", 6, 2, gid),
            ]),
        ];

        Assert.Equal([3, 6, 9], net.Select(c => c.AttachmentId));
        Assert.Equal(
            [AttachmentChangeKind.Delete, AttachmentChangeKind.Update, AttachmentChangeKind.Insert],
            net.Select(c => c.Kind));
        Assert.Equal(gid, net[1].GlobalId);
    }
}
