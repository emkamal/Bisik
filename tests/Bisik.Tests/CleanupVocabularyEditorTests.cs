using System.Windows.Forms;
using Bisik.Models;
using Bisik.UI;

namespace Bisik.Tests;

public class CleanupVocabularyEditorTests
{
    [Fact]
    public Task Structured_edits_round_trip_existing_profiles_and_commit_the_active_cell() => StaTestHost.RunAsync(() =>
    {
        using var form = new Form();
        using var editor = new CleanupVocabularyEditor { Dock = DockStyle.Fill };
        form.Controls.Add(editor); form.Show();
        editor.LoadVocabulary(CleanupVocabulary.AbbTemplate);
        Assert.Equal(CleanupVocabulary.AbbTemplate.Lines, editor.GetVocabulary()!.Lines);
        var grid = All(editor).OfType<DataGridView>().Single();
        grid.CurrentCell = grid.Rows[0].Cells[0]; grid.BeginEdit(true);
        ((TextBox)grid.EditingControl).Text = "NewCanonical";
        Assert.Equal("NewCanonical", editor.GetVocabulary()!.Entries[0].Canonical);
        Assert.NotEmpty(editor.GetVocabulary()!.Entries[0].Variants);
        editor.LoadVocabulary(null); Assert.Null(editor.GetVocabulary());
        All(editor).OfType<Button>().Single(b => b.Text == "Add term").PerformClick();
        Assert.True(grid.ContainsFocus);
        Assert.True(grid.IsCurrentCellInEditMode);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Invalid_rows_show_inline_feedback_and_block_saving() => StaTestHost.RunAsync(() =>
    {
        using var editor = new CleanupVocabularyEditor();
        var grid = All(editor).OfType<DataGridView>().Single();
        All(editor).OfType<TextBox>().Single().Text = "Project";
        grid.Rows.Add("", "API only", "mishearing");
        Assert.Contains("Row 1", Assert.Throws<ArgumentException>(() => editor.GetVocabulary()).Message);
        Assert.NotEmpty(grid.Rows[0].ErrorText);
        grid.Rows[0].Cells[0].Value = "Canonical";
        Assert.Single(editor.GetVocabulary()!.Entries);
        Assert.Empty(grid.Rows[0].ErrorText);
        grid.Rows[0].Cells[2].Value = string.Join(",", Enumerable.Range(0, 21).Select(i => $"term{i}"));
        Assert.Throws<ArgumentException>(() => editor.GetVocabulary());
        return Task.CompletedTask;
    });
    private static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));
}
