using Alfred.Core;

namespace Alfred.Core.Tests;

/// <summary>
/// Ported from the Mac's FileOrganizerTests.swift. Everything happens in a
/// temporary tree with a fake Recycle Bin, never in real folders or the real bin.
/// </summary>
public class FileOrganizerTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FileOrganizer _organizer;
    private readonly string _journal;

    public FileOrganizerTests()
    {
        _journal = Path.Combine(_tree.Base, "journal.json");
        _organizer = new FileOrganizer(() => _tree.Roots, new FileJournal(_journal), new FakeRecycleBin(_tree.Bin));
    }

    public void Dispose() => _tree.Dispose();

    private Guid LastId => _organizer.Journal.LastUndoable!.Id;

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    [Fact]
    public void RenameKeepsExtensionAndUndoes()
    {
        var original = _tree.File("Invoice-0823.pdf");
        var plan = _organizer.PlanRename(original, "Invoice August");
        Assert.Equal("Invoice August.pdf", Path.GetFileName(plan.Items[0].Target));
        Assert.False(plan.NeedsConfirmation);
        _organizer.Apply(plan);
        Assert.False(Exists(original));
        Assert.True(Exists(Path.Combine(_tree.Documents, "Invoice August.pdf")));

        _organizer.Undo(LastId);
        Assert.True(Exists(original));
        Assert.False(Exists(Path.Combine(_tree.Documents, "Invoice August.pdf")));
    }

    [Fact]
    public void ExplicitExtensionIsHonoured()
    {
        Assert.Equal("notes.txt", FileOrganizer.Renamed("notes.md", "notes.txt"));
        Assert.Equal("Meeting notes.md", FileOrganizer.Renamed("notes.md", "Meeting notes"));
    }

    [Fact]
    public void RenameNeverOverwrites()
    {
        var a = _tree.File("a.pdf", contents: "A");
        _tree.File("Report.pdf", contents: "existing");
        var plan = _organizer.PlanRename(a, "Report");
        Assert.Equal("Report 2.pdf", Path.GetFileName(plan.Items[0].Target));
        _organizer.Apply(plan);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_tree.Documents, "Report.pdf")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".hidden")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a\\b")]
    [InlineData("CON")]
    [InlineData("what?")]
    public void BadNamesRefused(string name)
    {
        var a = _tree.File($"a-{Guid.NewGuid():N}.pdf");
        Assert.Throws<ToolException>(() => _organizer.PlanRename(a, name));
    }

    [Fact]
    public void BatchMoveIntoNewFolderAndUndo()
    {
        var shots = Enumerable.Range(1, 3).Select(i => _tree.File($"Screenshot {i}.png", _tree.Downloads)).ToList();
        var folder = Path.Combine(_tree.Documents, "Receipts");
        var plan = _organizer.PlanTransfer(shots, copying: false, folder, create: true);
        Assert.True(plan.NeedsConfirmation);
        Assert.Equal("Moved 3 files to Receipts", plan.Summary);
        _organizer.Apply(plan);
        Assert.All(shots, s => Assert.False(Exists(s)));
        Assert.Equal(3, Directory.GetFiles(folder).Length);

        _organizer.Undo(LastId);
        Assert.All(shots, s => Assert.True(Exists(s)));
        Assert.False(Exists(folder)); // Empty again, so recycled.
    }

    [Fact]
    public void CopyAndUndo()
    {
        var a = _tree.File("plan.pptx", _tree.Downloads);
        _organizer.Apply(_organizer.PlanTransfer([a], copying: true, _tree.Documents, create: false));
        Assert.True(Exists(a));
        Assert.True(Exists(Path.Combine(_tree.Documents, "plan.pptx")));
        _organizer.Undo(LastId);
        Assert.True(Exists(a));
        Assert.False(Exists(Path.Combine(_tree.Documents, "plan.pptx")));
    }

    [Fact]
    public void MoveCollisionGetsSuffix()
    {
        var a = _tree.File("report.pdf", _tree.Downloads, "new");
        _tree.File("report.pdf", contents: "old");
        _organizer.Apply(_organizer.PlanTransfer([a], copying: false, _tree.Documents, create: false));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_tree.Documents, "report.pdf")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_tree.Documents, "report 2.pdf")));
    }

    [Fact]
    public void MovingIntoSameFolderIsRefused()
    {
        var a = _tree.File("x.pdf");
        Assert.Throws<ToolException>(() => _organizer.PlanTransfer([a], copying: false, _tree.Documents, create: false));
    }

    [Fact]
    public void CreateFolderAndUndo()
    {
        _organizer.Apply(_organizer.PlanCreateFolder("Taxes 2026", _tree.Documents));
        Assert.True(Directory.Exists(Path.Combine(_tree.Documents, "Taxes 2026")));
        _organizer.Undo(LastId);
        Assert.False(Directory.Exists(Path.Combine(_tree.Documents, "Taxes 2026")));
    }

    [Fact]
    public void RecycleBinIsTheOnlyRemovalAndUndoes()
    {
        var a = _tree.File("old draft.docx");
        _organizer.Apply(_organizer.PlanTrash([a]));
        Assert.False(Exists(a));
        Assert.Single(Directory.GetFileSystemEntries(_tree.Bin));
        _organizer.Undo(LastId);
        Assert.True(Exists(a));
    }

    [Fact]
    public void OutsideRootsRefused()
    {
        var outside = _tree.File("secret.txt", _tree.Base);
        Assert.Throws<ToolException>(() => _organizer.PlanTrash([outside]));
        Assert.Throws<ToolException>(() => _organizer.PlanTransfer([_tree.File("y.pdf")], copying: false, _tree.Base, create: false));
        Assert.Throws<ToolException>(() => _organizer.PlanTrash([Path.Combine(_tree.Documents, "..", "secret.txt")]));
    }

    [Fact]
    public void HiddenAndDeniedFoldersRefused()
    {
        Assert.Throws<ToolException>(() => _organizer.PlanTrash([_tree.File("h.txt", Path.Combine(_tree.Documents, ".hidden"))]));
        Assert.Throws<ToolException>(() => _organizer.PlanTrash([_tree.File("i.js", Path.Combine(_tree.Documents, "app", "node_modules"))]));
        Assert.Throws<ToolException>(() => _organizer.PlanTrash([_tree.File("s.json", Path.Combine(_tree.Documents, "AppData"))]));
    }

    [Fact]
    public void LinksPointingOutsideAreRefused()
    {
        var outside = _tree.File("outside.txt", _tree.Base);
        var link = Path.Combine(_tree.Documents, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // Windows without Developer Mode can't create symlinks.
        }
        Assert.Throws<ToolException>(() => _organizer.PlanTrash([link]));
        var folderLink = Path.Combine(_tree.Documents, "linked folder");
        Directory.CreateSymbolicLink(folderLink, _tree.Base);
        Assert.Throws<ToolException>(() => _organizer.PlanTrash([Path.Combine(folderLink, "outside.txt")]));
    }

    [Fact]
    public void MoreThanTwentyRefused()
    {
        var many = Enumerable.Range(1, 21).Select(i => _tree.File($"f{i}.txt", _tree.Downloads)).ToList();
        Assert.Throws<ToolException>(() => _organizer.PlanTransfer(many, copying: false, _tree.Documents, create: false));
        Assert.Throws<ToolException>(() => _organizer.PlanTrash(many));
    }

    /// <summary>A failure mid-batch reverses what was already done.</summary>
    [Fact]
    public void PartialFailureRollsBack()
    {
        var a = _tree.File("a.txt", _tree.Downloads);
        var b = _tree.File("b.txt", _tree.Downloads);
        var plan = _organizer.PlanTransfer([a, b], copying: false, _tree.Documents, create: false);
        File.Delete(b); // Disappears between planning and applying.
        Assert.Throws<ToolException>(() => _organizer.Apply(plan));
        Assert.True(Exists(a));
        Assert.False(Exists(Path.Combine(_tree.Documents, "a.txt")));
    }

    /// <summary>Undo works from the journal on disk, as after a crash and relaunch.</summary>
    [Fact]
    public void UndoSurvivesRestart()
    {
        var a = _tree.File("keep.pdf");
        _organizer.Apply(_organizer.PlanRename(a, "renamed"));
        var reloaded = new FileOrganizer(() => _tree.Roots, new FileJournal(_journal), new FakeRecycleBin(_tree.Bin));
        reloaded.Undo(reloaded.Journal.LastUndoable!.Id);
        Assert.True(Exists(a));
    }

    /// <summary>A step whose files changed since is left alone, not forced.</summary>
    [Fact]
    public void UndoLeavesChangedFilesAlone()
    {
        var a = _tree.File("x.txt");
        _organizer.Apply(_organizer.PlanRename(a, "y"));
        _tree.File("x.txt", contents: "someone recreated it");
        Assert.Contains("left alone", _organizer.Undo(LastId));
        Assert.True(Exists(Path.Combine(_tree.Documents, "y.txt")));
    }

    [Fact]
    public void UndoingTwiceIsRefused()
    {
        _organizer.Apply(_organizer.PlanRename(_tree.File("z.txt"), "zz"));
        var id = LastId;
        _organizer.Undo(id);
        Assert.Throws<ToolException>(() => _organizer.Undo(id));
    }
}

/// <summary>Ported from the Mac's OrganiseRoutingTests.swift, plus the tools end to end over a temporary tree.</summary>
public class FileToolTests : IDisposable
{
    private readonly World _world = new();

    public void Dispose() => _world.Dispose();

    private System.Text.Json.Nodes.JsonObject Args(string said) => _world.Direct(said)!.Value.Args;

    [Fact]
    public void Rename()
    {
        var match = _world.Direct("Rename my invoice from last month to Invoice August")!.Value;
        Assert.Equal("organiseFiles", match.Tool);
        Assert.Equal("rename", match.Args.Str("operation"));
        Assert.Equal("invoice", match.Args.Str("name"));
        Assert.Equal("lastMonth", match.Args.Str("period"));
        Assert.Equal("invoice august", match.Args.Str("newName"));
    }

    [Fact]
    public void MoveIntoNewFolder()
    {
        var args = Args("move my screenshots from today into a folder called receipts");
        Assert.Equal("move", args.Str("operation"));
        Assert.Equal("screenshot", args.Str("kind"));
        Assert.Equal("today", args.Str("period"));
        Assert.Equal("receipts", args.Str("destination"));
    }

    [Fact]
    public void PutAndCopy()
    {
        Assert.Equal("documents", Args("put the tax pdfs in downloads into documents").Str("destination"));
        Assert.Equal("copy", Args("copy my presentation to desktop").Str("operation"));
    }

    [Theory]
    [InlineData("delete my old draft pdf")]
    [InlineData("trash my screenshots from yesterday")]
    [InlineData("move my screenshots to the recycle bin")]
    [InlineData("get rid of my old invoices from last year")]
    [InlineData("bin my old budget spreadsheet")]
    public void Trash(string said) => Assert.Equal("trash", Args(said).Str("operation"));

    [Fact]
    public void CreateFolder()
    {
        var args = Args("create a folder called Taxes 2026 on my desktop");
        Assert.Equal("createFolder", args.Str("operation"));
        Assert.Equal("taxes 2026", args.Str("newName"));
        Assert.Equal("Desktop", args.Str("folder"));
    }

    [Theory]
    [InlineData("undo")]
    [InlineData("Undo that.")]
    [InlineData("put it back")]
    public void Undo(string said) => Assert.Equal("undoFileChange", _world.Direct(SpokenText.Clean(said))?.Tool);

    [Fact]
    public void OthersUnaffected()
    {
        Assert.Equal("openFile", _world.Direct("open my invoice from last month")?.Tool);
        Assert.Equal("findFiles", _world.Direct("find my tax documents from last year")?.Tool);
        Assert.Equal("openFile", _world.Direct("Foundations of process mining introduction.PDF")?.Tool);
        Assert.Equal("organiseFiles", _world.Direct("move my screenshots to receipts")?.Tool);
    }

    [Fact]
    public void EverythingMeansTheWholeFolder()
    {
        var args = Args("delete everything in downloads");
        Assert.Equal("trash", args.Str("operation"));
        Assert.Equal("", args.Str("name"));
        Assert.Equal("Downloads", args.Str("folder"));
    }

    [Theory]
    [InlineData("can you tidy up by moving the screenshots on my desktop into a screenshots folder")]
    [InlineData("please bin that old budget spreadsheet")]
    public void SentencesAreNotBareNames(string said) => Assert.NotEqual("openFile", _world.Direct(said)?.Tool);

    [Fact]
    public void DestinationCleaning()
    {
        Assert.Equal("receipts", Destinations.Clean("a new folder called Receipts"));
        Assert.Equal("projects", Destinations.Clean("the Projects folder"));
        Assert.Equal("desktop", Destinations.Clean("my desktop"));
    }

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("yes please", true)]
    [InlineData("Go ahead.", true)]
    [InlineData("no", false)]
    [InlineData("Cancel", false)]
    [InlineData("never mind", false)]
    [InlineData("open spotify", null)]
    [InlineData("yes open spotify", null)]
    public void Answers(string transcript, bool? expected) => Assert.Equal(expected, FileServices.Answer(transcript));

    private static OrganiseFilesArguments Organise(string operation, string name = "x", string? newName = null, string? destination = null) =>
        new(operation, name, NewName: newName, Destination: destination);

    /// <summary>From the Mac's model: "put on something to listen to" → rename "something".</summary>
    [Fact]
    public void InventedRenameIsRefused() =>
        Assert.False(OrganiseFilesTool.IsGrounded(Organise("rename", "something", "something else"), "put on something to listen to"));

    [Fact]
    public void SpokenChangesAreAllowed()
    {
        Assert.True(OrganiseFilesTool.IsGrounded(Organise("rename", newName: "CV"), "change the name of my resume to CV"));
        Assert.True(OrganiseFilesTool.IsGrounded(Organise("move", destination: "screenshots"), "tidy up by moving the screenshots into a screenshots folder"));
        Assert.True(OrganiseFilesTool.IsGrounded(Organise("trash"), "please bin that old budget spreadsheet"));
        Assert.False(OrganiseFilesTool.IsGrounded(Organise("move", destination: "Archive"), "move my screenshots somewhere"));
    }

    [Fact]
    public void KindWordsLeaveTheName() => Assert.Empty(FileQuery.FromText("screenshots", FileKind.Screenshot, null).Words);

    [Fact]
    public async Task OpensTheBestMatch()
    {
        _world.Tree.File("Invoice July.pdf", modified: DateTime.Now.AddDays(-40));
        var august = _world.Tree.File("Invoice August.pdf");
        var result = await _world.Say("open my invoice pdf");
        Assert.Equal("Opened Invoice August.pdf · 1 other match", result.Text);
        Assert.Equal([august], _world.Opened);
    }

    [Fact]
    public async Task FindListsTokensNotPaths()
    {
        _world.Tree.File("tax 2025.pdf");
        _world.Tree.File("tax 2024.pdf");
        var result = await _world.Say("find my tax files");
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, i => Assert.StartsWith("file_", i.Id));
        Assert.DoesNotContain(result.Items, i => i.Detail.Contains(_world.Tree.Base));
        Assert.StartsWith("Opened tax", await _world.Files.SelectAsync(result.Items[0].Id));
        _world.Files.Reset();
        await Assert.ThrowsAsync<ToolException>(() => _world.Files.SelectAsync(result.Items[1].Id));
    }

    [Fact]
    public async Task ScreenshotsFromToday()
    {
        _world.Tree.File("Screenshot 2026-09-27 101010.png", Path.Combine(_world.Tree.Documents, "Pictures"));
        _world.Tree.File("holiday.png", _world.Tree.Desktop);
        var result = await _world.Say("open my latest screenshot");
        Assert.StartsWith("Opened Screenshot", result.Text);
    }

    [Fact]
    public async Task RenameThenUndo()
    {
        var file = _world.Tree.File("test.txt", _world.Tree.Desktop);
        var renamed = await _world.Say("rename test txt to notes");
        Assert.Equal("Renamed test.txt to notes.txt · say “undo” to reverse", renamed.Text);
        Assert.True(renamed.Undoable);
        Assert.Equal("Undid: Renamed test.txt to notes.txt", (await _world.Say("undo that")).Text);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task SameNameInTwoFoldersAsksWhich()
    {
        _world.Tree.File("test.txt", Path.Combine(_world.Tree.Documents, "ProjectA"));
        _world.Tree.File("test.txt", Path.Combine(_world.Tree.Documents, "ProjectB"));
        var result = await _world.Say("rename test txt to notes");
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(["ProjectA in Documents", "ProjectB in Documents"], result.Items.Select(i => i.Detail).Order());
        Assert.StartsWith("Renamed test.txt to notes.txt", await _world.Files.SelectAsync(result.Items[0].Id));
    }

    [Fact]
    public async Task BatchesWaitForYes()
    {
        for (var i = 0; i < 3; i++) _world.Tree.File($"Screenshot {i}.png", _world.Tree.Desktop);
        var result = await _world.Say("delete my screenshots");
        Assert.Equal("Move 3 files to the Recycle Bin?", result.Text);
        Assert.NotNull(result.Confirmation);
        Assert.Equal(3, Directory.GetFiles(_world.Tree.Desktop).Length); // nothing yet
        Assert.StartsWith("Moved 3 files to the Recycle Bin", _world.Files.Confirm(result.Confirmation!));
        Assert.Empty(Directory.GetFiles(_world.Tree.Desktop));
        Assert.Throws<ToolException>(() => _world.Files.Confirm(result.Confirmation!)); // used up
    }

    [Fact]
    public async Task MoveIntoExistingFolderByName()
    {
        Directory.CreateDirectory(Path.Combine(_world.Tree.Documents, "Receipts"));
        _world.Tree.File("receipt 1.pdf", _world.Tree.Downloads);
        var result = await _world.Say("move my receipt pdf to receipts");
        Assert.StartsWith("Moved receipt 1.pdf to Receipts", result.Text);
        Assert.True(File.Exists(Path.Combine(_world.Tree.Documents, "Receipts", "receipt 1.pdf")));
    }

    [Fact]
    public async Task ContentRequestsAreDeclined() =>
        await Assert.ThrowsAsync<ToolException>(() => _world.Run("findFiles", "find the file that mentions my salary", ("name", "salary")));

    [Fact]
    public async Task NothingFoundSaysWhereItLooked()
    {
        var error = await Assert.ThrowsAsync<ToolException>(() => _world.Say("open my tax return pdf"));
        Assert.Contains("Documents, Downloads, Desktop", error.Message);
    }

    [Fact]
    public void IndexQueryIsMetadataOnly()
    {
        var sql = WindowsSearchSql.Build(FileQuery.FromText("o'brien 100%", FileKind.Pdf, FilePeriod.LastMonth), [@"C:\Users\me\Documents"]);
        Assert.Contains("SCOPE='file:C:\\Users\\me\\Documents'", sql);
        Assert.Contains("System.FileName LIKE '%brien%'", sql);
        Assert.Contains("System.FileName LIKE '%100%'", sql);
        Assert.Contains("System.FileExtension = '.pdf'", sql);
        Assert.Contains("System.DateModified >=", sql);
        Assert.DoesNotContain("Contents", sql);
        Assert.DoesNotContain("brien'", sql.Replace("''", ""));
    }
}

public class RoutineTests : IDisposable
{
    private readonly World _world = new();

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("I'm home", "I'm home")]
    [InlineData("i'm home.", "I'm home")]
    [InlineData("I am home", "I'm home")]
    [InlineData("hey I'm home now", "I'm home")]
    [InlineData("good night", "Good night")]
    [InlineData("Goodnight.", "Good night")]
    [InlineData("I'm going to bed", "Good night")]
    [InlineData("time to focus", "Focus")]
    public void MatchesTriggers(string said, string routine) => Assert.Equal(routine, Routines.Match(said, Routines.Examples)?.Name);

    [Theory]
    [InlineData("open Spotify")]
    [InlineData("what time do I usually get home from work on a Friday")]
    [InlineData("say good night to my friends on WhatsApp for me please")]
    [InlineData("home")]
    public void IgnoresOtherCommands(string said) => Assert.Null(Routines.Match(said, Routines.Examples));

    [Fact]
    public void DisabledRoutinesDontMatch()
    {
        var routine = Routines.Examples[0] with { };
        routine.Enabled = false;
        Assert.Null(Routines.Match("I'm home", [routine]));
    }

    [Fact]
    public void StepsBecomeToolCalls()
    {
        var plan = Routines.Plan(Routines.Examples[0], _world.Tools);
        Assert.Equal(["controlSpotify", "openApp"], plan.Steps.Select(s => s.Tool.Name));
        Assert.All(plan.Steps, s => Assert.Null(s.Transcript));
        Assert.Equal("Welcome home.", plan.Routine!.Closing);
        Assert.Equal("playPlaylist", plan.Steps[0].Arguments.Str("action"));
        Assert.Equal("Liked Songs", plan.Steps[0].Arguments.Str("query"));
    }

    [Fact]
    public async Task PercentStepsAreClampedAndRun()
    {
        var routine = new Routine { Name = "Loud", Triggers = ["loud"], Steps = [new(RoutineStepKind.SetVolume, Percent: 140)] };
        var plan = Routines.Plan(routine, _world.Tools);
        Assert.Equal("100", plan.Steps[0].Arguments.Str("value"));
        Assert.Equal("Volume 100%", (await plan.Steps[0].ExecuteAsync(true, CancellationToken.None)).Text);
    }

    [Fact]
    public void MissingToolsAndEmptyStepsAreSkipped()
    {
        var routine = new Routine { Name = "R", Triggers = ["r"], Steps = [new(RoutineStepKind.OpenApp, ""), new(RoutineStepKind.LockScreen), new(RoutineStepKind.PlayMusic)] };
        var plan = Routines.Plan(routine, _world.Tools.Where(t => t.Name != "controlSpotify").ToList());
        Assert.Equal(["systemControl"], plan.Steps.Select(s => s.Tool.Name));
        Assert.Equal(["Open app: ", "Play music"], plan.Routine!.Skipped);
    }

    [Fact]
    public void RoutinesRoundTripThroughSettings()
    {
        var file = Path.Combine(_world.Tree.Base, "settings.json");
        new SettingsStore(file).Update(s => s.Routines = [.. Routines.Examples]);
        var loaded = new SettingsStore(file).Current.Routines;
        Assert.Equal(Routines.Examples.Select(r => r.Name), loaded.Select(r => r.Name));
        Assert.Equal(Routines.Examples[0].Steps, loaded[0].Steps);
    }
}

public class WakePhraseTests
{
    [Theory]
    [InlineData("Alfred open Spotify", "open Spotify")]
    [InlineData("Hey Alfred, play some music", "play some music")]
    [InlineData("Okay Alfredo what's the weather", "what's the weather")]
    [InlineData("Al fred open YouTube", "open YouTube")]
    [InlineData("Good morning Alfred", "Good morning Alfred")]
    [InlineData("Alfred", "Alfred")]
    public void StripsTheWakeWord(string transcript, string command) => Assert.Equal(command, WakePhrase.Command(transcript));

    [Theory]
    [InlineData("open Spotify")]
    [InlineData("all friends are here")]
    [InlineData("he offered me coffee")]
    [InlineData("")]
    public void RejectsFalseDetections(string transcript) => Assert.Null(WakePhrase.Command(transcript));

    [Fact]
    public void WakeWordMustComeEarly()
    {
        Assert.Null(WakePhrase.Command("one two three four five six seven eight nine ten eleven Alfred"));
        Assert.Equal("open notes", WakePhrase.Command("one two three four five six Alfred open notes"));
    }

    [Theory]
    [InlineData("Okay, Alfred.")]
    [InlineData("K. Alfred, play some music.")]
    [InlineData("Good morning, Alfred.")]
    [InlineData("so anyway hey alfred open spotify")]
    public void Finds(string text) => Assert.True(WakePhrase.Contains(text));

    [Theory]
    [InlineData("He offered me a coffee this afternoon.")]
    [InlineData("How are you?")]
    [InlineData("all friends are here")]
    [InlineData("")]
    public void Ignores(string text) => Assert.False(WakePhrase.Contains(text));

    [Theory]
    [InlineData("I said open Petnet properties.PDF", "open Petnet properties.PDF")]
    [InlineData("Hi friend open process mini tutorial.PDF", "open process mini tutorial.PDF")]
    [InlineData("Play Matt Armstrong's latest video on YouTube", "Play Matt Armstrong's latest video on YouTube")]
    [InlineData("Alfred play some music", "play some music")]
    [InlineData("Hey Alfred, turn on DND", "turn on DND")]
    [InlineData("Good morning", "Good morning")]
    public void TidiesConfirmedWakes(string transcript, string command) => Assert.Equal(command, WakePhrase.CommandAfterConfirmedWake(transcript));

    [Fact]
    public void EndpointerEndsAfterTrailingSilence()
    {
        var endpointer = new Endpointer();
        Assert.Equal(Endpointer.Decision.Listening, endpointer.Feed(true, 0.5));
        Assert.Equal(Endpointer.Decision.Listening, endpointer.Feed(false, 0.5));
        Assert.Equal(Endpointer.Decision.EndOfSpeech, endpointer.Feed(false, 0.4));
    }

    [Fact]
    public void EndpointerGivesUpWithoutSpeech()
    {
        var endpointer = new Endpointer();
        Assert.Equal(Endpointer.Decision.Listening, endpointer.Feed(false, 1.5));
        Assert.Equal(Endpointer.Decision.NoSpeech, endpointer.Feed(false, 0.6));
    }

    [Fact]
    public void EndpointerHasAHardCap()
    {
        var endpointer = new Endpointer();
        var decision = Endpointer.Decision.Listening;
        for (var i = 0; i < 400 && decision == Endpointer.Decision.Listening; i++) decision = endpointer.Feed(true, 0.03);
        Assert.Equal(Endpointer.Decision.EndOfSpeech, decision);
    }

    [Fact]
    public void EnergyGateCalibratesToTheRoom()
    {
        var gate = new EnergyGate();
        var quiet = Enumerable.Repeat(0.001f, 480).ToArray();
        var loud = Enumerable.Repeat(0.3f, 480).ToArray();
        for (var i = 0; i < 7; i++) gate.IsSpeech(quiet, 0.03);
        Assert.False(gate.IsSpeech(quiet, 0.03));
        Assert.True(gate.IsSpeech(loud, 0.03));
    }

    [Fact]
    public void PowerProfiles()
    {
        Assert.True(PowerProfile.PluggedIn.AllowsWakeWord(false));
        Assert.False(PowerProfile.Battery.AllowsWakeWord(false));
        Assert.True(PowerProfile.Battery.AllowsWakeWord(true));
        Assert.False(PowerProfile.BatterySaver.AllowsWakeWord(true));
        Assert.Null(PowerProfile.PluggedIn.UnloadAfter());
        Assert.Equal(TimeSpan.FromMinutes(2), PowerProfile.Battery.UnloadAfter());
    }
}
