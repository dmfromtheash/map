using Map.Core.Documents;

namespace Map.Core.Editing;

public sealed class AddNoteCommand(MapNote note) : IEditCommand
{
    public string Name => "Add note";
    public MapDocument Apply(MapDocument document) => document.WithNotes([.. document.Notes, note]);
    public MapDocument Revert(MapDocument document) => document.WithNotes([.. document.Notes.Where(item => item.Id != note.Id)]);
}

public sealed class ReplaceNoteCommand(MapNote before, MapNote after) : IEditCommand
{
    public string Name => "Edit note";
    public MapDocument Apply(MapDocument document) => Write(document, after);
    public MapDocument Revert(MapDocument document) => Write(document, before);
    private MapDocument Write(MapDocument document, MapNote value) => document.WithNotes(
        [.. document.Notes.Select(item => item.Id == value.Id ? value : item)]);
}

public sealed class DeleteNoteCommand(MapDocument document, NoteId id) : IEditCommand
{
    private readonly int _index = document.Notes.ToList().FindIndex(note => note.Id == id);
    private readonly MapNote _note = document.Notes.First(note => note.Id == id);
    public string Name => "Delete note";
    public MapDocument Apply(MapDocument value) => value.WithNotes([.. value.Notes.Where(note => note.Id != id)]);
    public MapDocument Revert(MapDocument value)
    {
        var notes = value.Notes.ToList(); notes.Insert(Math.Min(_index, notes.Count), _note);
        return value.WithNotes(notes);
    }
}
