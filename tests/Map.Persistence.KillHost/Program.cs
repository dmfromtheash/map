using Map.Core.Documents;
using Map.Persistence;
using Map.Persistence.Recovery;

// Child process of the real process-kill gate. It establishes a last-good target, starts a save
// of a CHANGED document, prints READY after the journal records TempDurable (temp written,
// flushed, validated), then blocks before the replace until the parent kills it. Being killed is
// its purpose: no finally block or flush-on-exit may run.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Map.Persistence.KillHost <targetPath> <journalRoot>");
    return 2;
}

var targetPath = args[0];
var journal = new RecoveryJournal(args[1]);

var documentId = DocumentId.Parse("11111111-2222-3333-4444-555555555555");
var layerId = LayerId.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
var objectId = ObjectId.Parse("01234567-89ab-cdef-0123-456789abcdef");
var createdUtc = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

MapDocument Build(string title, double x) => new(
    documentId, title, createdUtc,
    new CanvasDefinition(1024, 768, "px", "#FF203040"),
    [
        new Layer(layerId, "objects", visible: true, locked: false, opacity: 1.0,
        [
            new SceneObject(
                objectId, SceneObjectKind.Rectangle, "witness",
                x, 50, 200, 100, 15, "#FF3E5C76", "#FF1B263B", 2,
                visible: true, locked: false),
        ]),
    ]);

byte[] Preview() =>
[
    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
    0x08, 0x06, 0x00, 0x00, 0x00,
    0x1F, 0x15, 0xC4, 0x89,
    1, 2, 3, 4, 5, 6, 7, 8,
];

var metadata = new SaveMetadata("killhost", new DateTimeOffset(2026, 8, 25, 13, 0, 0, TimeSpan.Zero));

// 1. Last-good target: title "old", x = 100.
if (!File.Exists(targetPath))
{
    MapProjectStore.Save(Build("old", 100), targetPath, metadata, Preview(), journal);
}

Console.WriteLine("BASELINE");
Console.Out.Flush();

// 2. Save the NEW version and block at the controlled boundary until killed.
var faults = new SaveFaultInjection
{
    AfterDurableTempBeforeReplace = () =>
    {
        Console.WriteLine("READY");
        Console.Out.Flush();
        Thread.Sleep(TimeSpan.FromMinutes(2)); // parent kills us here
    },
};

MapProjectStore.Save(Build("new", 400), targetPath, metadata, Preview(), journal, faults);

// Unreachable when the gate works: the parent kills the process during the sleep.
Console.WriteLine("ERROR-NOT-KILLED");
return 3;
