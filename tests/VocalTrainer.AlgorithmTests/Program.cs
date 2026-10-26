using VocalTrainer;
using VocalTrainer.Practice;
using VocalTrainer.ViewModels;
using VocalTrainer.Infrastructure;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
    Console.WriteLine("OK " + message);
}

static void Rejects<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, message); return; }
    throw new Exception("FAILED: " + message);
}

Check(NoteCatalog.ToMidi(0, 4) == 60, "Middle C C4=MIDI60");
Check(Math.Abs(NoteCatalog.ToFrequency(69) - 440) < 0.00001, "A4=440Hz");
Check(NoteCatalog.ToRussian(48).Contains("До") && NoteCatalog.ToRussian(48).Contains("малая"), "Russian C3 label");
Check(ExerciseIO.ParseMidi("C3") == 48, "C3=48");
Check(ExerciseIO.ParseMidi("F#4") == 66, "F#4=66");
Check(ExerciseIO.ParseMidi("Bb3") == 58, "Bb3=58");
Check(ExerciseIO.ParseMidi("Db4") == 61, "Db4=61");
Check(ExerciseIO.ParseMidi("C-1") == 0, "C-1 lowest midi");
Check(Math.Abs(NoteCatalog.Cents(440 * Math.Pow(2, 1.0 / 12), 440) - 100) < 0.00001, "semitone=100c");

var exercise = new ExerciseDocument
{
    Title = "Тест",
    Sections = [new ExerciseSection { Title = "Первая", Notes = [new ExerciseNote { Note = "D3" }] }]
};
ExerciseIO.Validate(exercise);
Check(true, "exercise schema valid");
try { ExerciseIO.ParseMidi("Q2"); throw new Exception("Invalid note was accepted"); }
catch (FormatException) { Check(true, "invalid note rejected"); }
try { ExerciseIO.Validate(new ExerciseDocument { Title = "", Sections = exercise.Sections }); throw new Exception("Invalid exercise was accepted"); }
catch (FormatException) { Check(true, "invalid exercise rejected"); }

foreach (double hz in new[] { 82.40689, 130.81278, 146.83238, 164.81378, 220.0, 440.0, 880.0 })
{
    var samples = new float[PitchDetector.FrameSamples];
    for (int i = 0; i < samples.Length; i++)
        samples[i] = (float)(0.32 * Math.Sin(2 * Math.PI * hz * i / PitchDetector.SampleRate));
    var pitch = PitchDetector.Detect(samples);
    Check(pitch.FrequencyHz is { } measured && Math.Abs(NoteCatalog.Cents(measured, hz)) < 12,
        $"YIN sine {hz:F2}Hz accurate within 12 cents (actual {pitch.FrequencyHz:F1}Hz)");
}
Check(!PitchDetector.Detect(new float[PitchDetector.FrameSamples]).IsVoiced, "silence gate");
var settings = new AssessmentSettings(30, .75);
var accurate = new AttemptAssessment(48, settings, 0);
for (int i = 0; i < 35; i++) accurate.Add(new PitchMeasurement(NoteCatalog.ToFrequency(48), .09, .95), 120L * i);
var accurateResult = accurate.Finish();
Check(accurateResult.Success && accurateResult.AccuratePercent == 100, "perfect pitch meets time-in-tolerance threshold");
Check(accurateResult.TimeToLockSeconds.HasValue && accurateResult.LongestStableSeconds > 3,
    "sustained pitch lock and longest stable stretch");

var sharp = new AttemptAssessment(48, settings, 0);
for (int i = 0; i < 30; i++) sharp.Add(new PitchMeasurement(NoteCatalog.ToFrequency(49), .09, .98), 120L * i);
Check(!sharp.Finish().Success && sharp.Finish().AccuratePercent == 0,
    "100 cents sharp does not pass 30 cents tolerance");

var silence = new AttemptAssessment(48, settings, 0);
for (int i = 0; i < 30; i++) silence.Add(new PitchMeasurement(null, .002, 0), 120L * i);
Check(!silence.Finish().Success && silence.Finish().VoiceCoveragePercent == 0,
    "no signal is not counted as a passing attempt");

var unreliable = new AttemptAssessment(48, settings, 0);
for (int i = 0; i < 30; i++) unreliable.Add(new PitchMeasurement(NoteCatalog.ToFrequency(48), .02, .2), 120L * i);
Check(!unreliable.Finish().Success && unreliable.Finish().AccuratePercent == 0,
    "low-confidence microphone estimates excluded from scoring");

var delayed = new AttemptAssessment(48, settings, 0);
for (int i = 0; i < 5; i++) delayed.Add(new PitchMeasurement(NoteCatalog.ToFrequency(49), .1, .95), 120L * i);
for (int i = 5; i < 35; i++) delayed.Add(new PitchMeasurement(NoteCatalog.ToFrequency(48), .1, .95), 120L * i);
var delayedResult = delayed.Finish();
Check(delayedResult.Success && delayedResult.TimeToLockSeconds is >= .5 and <= 1,
    "finds pitch after initial correction and measures acquisition time");
Check(delayedResult.InitialDeviationCents is > 90 and < 110,
    "first detected error preserved as initial deviation");

var newExercise = new ExerciseDocument { Title = "Rules", Sections = [new ExerciseSection {
    Title = "Rules", Notes = [new ExerciseNote { Note = "C3", ToleranceCents = 30,
        RequiredAccuracy = .80, AutoRepeatOnFail = true, MaxAttempts = 3 }]
}] };
ExerciseIO.Validate(newExercise);
string serialized = System.Text.Json.JsonSerializer.Serialize(newExercise, ExerciseIO.Options);
Check(serialized.Contains("toleranceCents") && serialized.Contains("autoRepeatOnFail"),
    "v0.3 scoring rules serialize as camelCase JSON");
try {
    newExercise.Sections[0].Notes[0].MaxAttempts = 9;
    ExerciseIO.Validate(newExercise);
    throw new Exception("invalid attempt limit accepted");
} catch (FormatException) { Check(true, "invalid v0.3 JSON rules rejected"); }
Console.WriteLine("All algorithm tests passed.");


var sameChallenge = new EarChallenge(48, 48);
Check(sameChallenge.Direction == PitchDirection.Same, "identical pitches classified as same");
var descending = new EarChallenge(48, 45);
Check(descending.Direction == PitchDirection.Lower && EarTrainingLogic.Evaluate(descending, PitchDirection.Lower),
    "ear training response matching");
for (int i = 0; i < 120; i++)
{
    EarChallenge challenge = EarTrainingLogic.Create(new Random(i), 0, 12, true);
    Check(challenge.SecondMidi is >= 0 and <= 127, $"ear edge MIDI stays valid case {i}");
}
var rangeSamples = Enumerable.Range(0, 30)
    .Select(_ => new RangeSample(NoteCatalog.ToFrequency(48), .99)).ToArray();
var rangeResult = RangeMeasurementResult.Analyze(48, rangeSamples);
Check(rangeResult.Accurate && rangeResult.VoiceCoveragePercent == 100 && rangeResult.MedianDeviationCents == 0,
    "range accurate, 100% voiced");
var badRange = RangeMeasurementResult.Analyze(48, Enumerable.Repeat(new RangeSample(null, 0), 30).ToArray());
Check(!badRange.Accurate && badRange.MedianDeviationCents is null, "range rejects silence");
var outOfTune = RangeMeasurementResult.Analyze(48,
    Enumerable.Repeat(new RangeSample(NoteCatalog.ToFrequency(50), .99), 30).ToArray());
Check(!outOfTune.Accurate && outOfTune.MedianDeviationCents > 150, "range rejects > one semitone error");
newExercise.Sections[0].Notes[0].MaxAttempts = 3;
var editable = ExerciseEditorDocument.Unpack(newExercise);
var rebuilt = ExerciseEditorDocument.Build("Пример", "Описание", "Цель 1\nЦель 2", "", editable);
Check(rebuilt.Sections[0].Notes[0].AutoRepeatOnFail == true && rebuilt.Goals?.Count == 2,
    "JSON editor round-trip preserves scoring rules and optional lists");
try
{
    editable[0].Notes[0].Note = "H2";
    ExerciseEditorDocument.Build("Тест", "", "", "", editable);
    throw new Exception("Invalid editor note accepted");
}
catch (FormatException) { Check(true, "editor prevents invalid notes"); }
var folder = Path.Combine(Path.GetTempPath(), "vocal-trainer-test-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new RangeHistoryStore(folder);
    store.Append(new RangeHistoryEntry(DateTimeOffset.UtcNow, 48, 100, 0, 5, "easy"));
    Check(store.Load().Count == 1 && store.Load()[0].Midi == 48, "local range map round-trip");
}
finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

// v0.5 — SMF decoding, tempo map, singing guide, loop scoring.
var midiPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "two-tempo.mid");
var imported = VocalTrainer.Melody.MidiSongReader.Read(midiPath);
Check(imported.Tracks.Count == 1 && imported.Tracks[0].Notes.Count == 8,
    "SMF format 1 imports notes while excluding tempo-only track");
var vocal = imported.Tracks[0];
Check(vocal.Title == "Vocal Melody" && vocal.OverlapCount == 0, "vocal track label and monophonic shape");
Check(Math.Abs(vocal.Notes[1].StartSeconds - .5) < .000001 &&
      Math.Abs(vocal.Notes[5].StartSeconds - 2.6) < .000001,
    "global tempo map handles change at beat 4: 120 -> 100 BPM");
Check(Math.Abs(vocal.EndSeconds - 4.4) < .000001, "MIDI duration reflects both tempos");
var slice = VocalTrainer.Melody.MelodyPlanner.Build(vocal, .5, -2, .5, 2.6);
Check(slice.Count == 4 && slice[0].Midi == 48 && Math.Abs(slice[0].StartSeconds) < .00001,
    "selected MIDI fragment is trimmed, transposed -2 and time-stretched to half speed");
Check(Math.Abs(slice[0].DurationSeconds - 1.0) < .00001,
    "duration of MIDI note doubles at 50% speed");
Check(VocalTrainer.Melody.MelodyPlanner.At(slice, 0.25)?.Midi == 48 &&
      VocalTrainer.Melody.MelodyPlanner.At(slice, 12) is null,
    "lookup returns current monophonic pitch or silence");
try
{
    VocalTrainer.Melody.MelodyPlanner.Build(vocal, 2, 0, 0, vocal.EndSeconds);
    throw new Exception("Invalid speed accepted");
}
catch (ArgumentOutOfRangeException) { Check(true, "speed outside 50-150% rejected"); }
try
{
    VocalTrainer.Melody.MidiSongReader.ReadBytes([0, 1, 2, 3]);
    throw new Exception("Corrupt MIDI accepted");
}
catch (FormatException) { Check(true, "corrupt MIDI rejected"); }
var scoring = new VocalTrainer.Melody.MelodyScorer(slice, 40);
foreach (var target in slice)
{
    for (int k = 0; k < 8; k++) scoring.Add(target.StartSeconds + (k + .5) * target.DurationSeconds / 8,
        NoteCatalog.ToFrequency(target.Midi), .96);
}
var resultMelody = scoring.Finish();
Check(resultMelody.Passed == slice.Count && resultMelody.AverageAccuracy == 100 &&
      resultMelody.AverageCoverage == 100, "perfect sung melody passes every target");
var silenceMelody = new VocalTrainer.Melody.MelodyScorer(slice, 40);
foreach (var target in slice) silenceMelody.Add(target.StartSeconds + .05, null, 0);
Check(silenceMelody.Finish().Passed == 0, "silence never receives credit for MIDI notes");
Console.WriteLine("Vocal Trainer v0.5 MIDI tests passed.");

// v0.5.1 — shortcuts: scope resolution, duplicate protection, persistence, logical pause.
var shortcuts = VocalTrainer.Infrastructure.ShortcutCatalog.Defaults();

Check(VocalTrainer.Infrastructure.ShortcutCatalog.Validate(shortcuts) is null, "default shortcut scheme has no overlapping gestures");
Check(VocalTrainer.Infrastructure.ShortcutCatalog.Resolve(shortcuts, new("Space"), "exercises") == "trainer_toggle" &&
      VocalTrainer.Infrastructure.ShortcutCatalog.Resolve(shortcuts, new("Space"), "midi") == "midi_toggle" &&
      VocalTrainer.Infrastructure.ShortcutCatalog.Resolve(shortcuts, new("Space"), "editor") is null,
      "Space is context-sensitive and not global");
Check(VocalTrainer.Infrastructure.ShortcutCatalog.Resolve(shortcuts, new("D1"), "ear") == "ear_higher" &&
      VocalTrainer.Infrastructure.ShortcutCatalog.Resolve(shortcuts, new("D1", Ctrl:true), "ear") == "tab1",
      "numeric ear answers do not conflict with Ctrl+tab shortcuts");
shortcuts["trainer_repeat"] = new("Space");
Check(VocalTrainer.Infrastructure.ShortcutCatalog.Validate(shortcuts) is not null, "duplicate keys rejected within one context");
shortcuts = VocalTrainer.Infrastructure.ShortcutCatalog.Defaults();
shortcuts["help"] = new("R");
Check(VocalTrainer.Infrastructure.ShortcutCatalog.Validate(shortcuts) is not null, "global key collision with a scoped key rejected");
shortcuts = VocalTrainer.Infrastructure.ShortcutCatalog.Defaults();
shortcuts["help"] = new("F4", Alt:true);
Check(VocalTrainer.Infrastructure.ShortcutCatalog.Validate(shortcuts) is not null, "Alt+F4 reserved");
var settingsDir = Path.Combine(Path.GetTempPath(), "vocal-shortcuts-test-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new VocalTrainer.Infrastructure.ShortcutStore(Path.Combine(settingsDir, "settings.json"));
    shortcuts = VocalTrainer.Infrastructure.ShortcutCatalog.Defaults();
    shortcuts["trainer_repeat"] = new("F6");
    store.Save(shortcuts);
    Check(store.Load()["trainer_repeat"].Display == "F6", "shortcut settings JSON round-trip");
}
finally { if (Directory.Exists(settingsDir)) Directory.Delete(settingsDir, true); }

var pausedClock = new VocalTrainer.Infrastructure.PausableClock();
await Task.Delay(35);
pausedClock.Pause();
double pausedAt = pausedClock.ElapsedSeconds;
await Task.Delay(90);
Check(Math.Abs(pausedClock.ElapsedSeconds - pausedAt) < .004, "paused clock excludes wall time");
pausedClock.Resume();
await pausedClock.DelayAsync(.045, CancellationToken.None);
Check(pausedClock.ElapsedSeconds >= pausedAt + .04, "clock resumes at logical time");
pausedClock.Stop();
Console.WriteLine("Vocal Trainer v0.5.1 shortcut and pause tests passed.");

// Regressions found during the first Windows build and runtime review.
Rejects<FormatException>(() => ExerciseIO.Validate(new ExerciseDocument
{
    Title = "Null description", Description = null!, Sections = exercise.Sections
}), "null JSON description produces a validation error");
Rejects<FormatException>(() => ExerciseIO.Validate(new ExerciseDocument
{
    Title = "Null goal", Goals = [null!], Sections = exercise.Sections
}), "null JSON goals rejected");

var repeatedNotes = new VocalTrainer.Melody.MidiTrack(0, "Repeated C3",
    [new(48, 0, .5, 0), new(48, .5, .5, 0)], 0);
var repeatedTargets = VocalTrainer.Melody.MelodyPlanner.Build(repeatedNotes, 1, 0, 0, 1);
Check(repeatedTargets.Count == 2 && repeatedTargets[0].DurationSeconds == .5 &&
    repeatedTargets[1].StartSeconds == .5, "adjacent repeated MIDI notes keep separate attacks and scores");
var overlappingNotes = new VocalTrainer.Melody.MidiTrack(0, "Hidden lower note",
    [new(60, 0, 1, 0), new(48, .25, .5, 0)], 1);
Check(VocalTrainer.Melody.MelodyPlanner.Build(overlappingNotes, 1, 0, 0, 1).Count == 1,
    "lower overlapping notes do not split a sustained upper target");
Rejects<ArgumentException>(() => VocalTrainer.Melody.MelodyPlanner.Build(vocal, 1, 0, double.NaN, 1),
    "NaN fragment start rejected");
Rejects<ArgumentException>(() => VocalTrainer.Melody.MelodyPlanner.Build(vocal, 1, 0, 0, double.NaN),
    "NaN fragment end rejected");
var invalidFrequencyScore = new VocalTrainer.Melody.MelodyScorer(repeatedTargets);
invalidFrequencyScore.Add(.1, double.PositiveInfinity, .99);
invalidFrequencyScore.Add(.2, NoteCatalog.ToFrequency(48), double.PositiveInfinity);
Check(invalidFrequencyScore.Finish().Notes[0].Voiced == 0,
    "non-finite pitch observations excluded from MIDI scores");

var regressionFolder = Path.Combine(Path.GetTempPath(), "vocal-regressions-" + Guid.NewGuid().ToString("N"));
try
{
    var history = new RangeHistoryStore(regressionFolder);
    File.WriteAllText(Path.Combine(regressionFolder, "range-history.json"), "[null]");
    Check(history.Load().Count == 0, "null range history entries cannot crash startup");
    using (var logger = new SessionLogger(regressionFolder, 48, "Synthetic microphone"))
    {
        logger.Sample(48, new PitchMeasurement(NoteCatalog.ToFrequency(48), .1, .99));
        logger.SetPhase("hold");
        logger.BeginStep(0, 0, 48, new ExerciseNote());
        logger.Sample(48, new PitchMeasurement(null, 0, 0));
        logger.EndStep(0, 0, 48);
        logger.Finish();
        var events = File.ReadAllLines(logger.FilePath)
            .Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
        try
        {
            Check(events[^1].RootElement.GetProperty("type").GetString() == "session_end" &&
                events[^1].RootElement.GetProperty("details").GetProperty("observations").GetInt32() == 2,
                "session logger writes valid JSONL and final statistics");
        }
        finally { foreach (var item in events) item.Dispose(); }
    }
    Check(Directory.GetFiles(regressionFolder, "*.wav").Length == 0,
        "session logging does not create audio files");
}
finally { if (Directory.Exists(regressionFolder)) Directory.Delete(regressionFolder, true); }

var busyStates = new List<bool>();
var midiEvents = new List<string>();
var midiErrors = new List<Exception>();
var referenceTones = new List<double>();
using (var melodyVm = new VocalTrainer.ViewModels.MelodyViewModel((hz, _) => referenceTones.Add(hz), () => { },
    () => true, () => true, () => midiPath, (kind, _) => midiEvents.Add(kind), (_, ex) => midiErrors.Add(ex)))
{
    melodyVm.ImportCommand.Execute(null);
    var originalTargets = melodyVm.Targets.ToArray();
    melodyVm.TransposeText = "+3";
    melodyVm.ApplyTransposeCommand.Execute(null);
    Check(melodyVm.Transpose == 3 && melodyVm.Targets.Select(n => n.Midi).SequenceEqual(originalTargets.Select(n => n.Midi + 3)) &&
        melodyVm.Targets.Select(n => (n.StartSeconds, n.DurationSeconds)).SequenceEqual(originalTargets.Select(n => (n.StartSeconds, n.DurationSeconds))),
        "numeric transpose shifts every note without changing timing");
    melodyVm.PreviewCommand.Execute(null);
    Check(referenceTones.Count > 0 && Math.Abs(referenceTones[0] - NoteCatalog.ToFrequency(originalTargets[0].Midi + 3)) < .001,
        "MIDI reference playback uses the transposed pitch");
    melodyVm.Stop();
    var previewDeadline = System.Diagnostics.Stopwatch.StartNew();
    while (melodyVm.IsBusy && previewDeadline.Elapsed.TotalSeconds < 3) await Task.Delay(20);
    Check(!melodyVm.IsBusy, "transposed reference stops cleanly");
    var appliedTargets = melodyVm.Targets;
    melodyVm.TransposeText = "127";
    melodyVm.ApplyTransposeCommand.Execute(null);
    Check(melodyVm.Transpose == 3 && ReferenceEquals(appliedTargets, melodyVm.Targets),
        "invalid transpose leaves the applied score unchanged");
    melodyVm.TransposeText = "1.5";
    melodyVm.ApplyTransposeCommand.Execute(null);
    Check(melodyVm.Transpose == 3, "non-integer transpose rejected");
    melodyVm.TransposeText = "−12";
    melodyVm.ApplyTransposeCommand.Execute(null);
    Check(melodyVm.Transpose == -12, "downward octave shift accepts Unicode minus");
    melodyVm.TransposeUpCommand.Execute(null);
    Check(melodyVm.Transpose == -11, "up button moves the entire part by one semitone");
    melodyVm.TransposeDownCommand.Execute(null);
    melodyVm.ResetTransposeCommand.Execute(null);
    Check(melodyVm.Transpose == 0 && melodyVm.Targets.SequenceEqual(originalTargets), "reset restores original MIDI notes");
    int tonesBeforeSoloPractice = referenceTones.Count;
    melodyVm.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(melodyVm.IsBusy)) busyStates.Add(melodyVm.IsBusy);
    };
    melodyVm.PracticeCommand.Execute(null);
    Check(melodyVm.IsCountingIn && !melodyVm.ImportCommand.CanExecute(null),
        "MIDI count-in disables conflicting commands");
    melodyVm.Transpose = 5;
    Check(melodyVm.Transpose == 0 && !melodyVm.ApplyTransposeCommand.CanExecute(null), "transpose cannot change during a take");
    var handoffDeadline = System.Diagnostics.Stopwatch.StartNew();
    while (!melodyVm.IsPracticing && handoffDeadline.Elapsed.TotalSeconds < 6) await Task.Delay(25);
    Check(melodyVm.IsPracticing && busyStates.All(state => state),
        "MIDI count-in hands off to practice without exposing an idle state");
    melodyVm.OnMicrophoneSample(NoteCatalog.ToFrequency(vocal.Notes[0].Midi), .99);
    Check(melodyVm.Voice.Count == 1 && Math.Abs(melodyVm.Voice[0].MidiPitch!.Value - vocal.Notes[0].Midi) < .001,
        "microphone pitch reaches the MIDI overlay in semitones");
    melodyVm.OnMicrophoneSample(null, 0);
    Check(melodyVm.Voice.Count == 2 && melodyVm.Voice[^1].MidiPitch is null, "silence inserts a visible trace break");
    Check(referenceTones.Count == tonesBeforeSoloPractice, "solo practice does not play a reference tone");
    melodyVm.TogglePlaybackPause();
    double frozenCursor = melodyVm.Cursor;
    await Task.Delay(80);
    melodyVm.OnMicrophoneSample(NoteCatalog.ToFrequency(48), .99);
    Check(melodyVm.IsPaused && melodyVm.Cursor == frozenCursor, "paused MIDI practice does not advance");
    melodyVm.TogglePlaybackPause();
    melodyVm.Stop();
    Check(melodyVm.Voice.Count == 2, "stopping preserves the performed trace for review");
    Check(!melodyVm.IsBusy && melodyVm.CanPractice && midiEvents.Contains("midi_practice_end") && midiErrors.Count == 0,
        "MIDI practice stops, logs its result and enables commands");
    melodyVm.Dispose();
    melodyVm.Repeat();
    Check(!melodyVm.IsBusy && !melodyVm.CanImport && !melodyVm.CanPractice,
        "disposed MIDI trainer cannot restart playback");
}
var wideShift = VocalTrainer.Melody.MelodyPlanner.Build(vocal, 1, 24, 0, vocal.EndSeconds);
Check(wideShift[0].Midi == vocal.Notes[0].Midi + 24, "composition can move by more than one octave");
var boundaryTrack = new VocalTrainer.Melody.MidiTrack(0, "Boundary", [new(0, 0, 1, 0), new(60, 1, 1, 0)], 0);
Rejects<ArgumentException>(() => VocalTrainer.Melody.MelodyPlanner.Build(boundaryTrack, 1, -1, 1, 2),
    "transpose validates the whole part even outside the selected fragment");
var overlaySamples = new VocalTrainer.Melody.VoiceSample[]
{
    new(0, 60, 0), new(.12, 60.2, 20), new(.24, null, null), new(.36, 48, -1200),
    new(.48, 48.1, -1190), new(1, 72, 1200), new(1.12, double.NaN, null)
};
var strokes = VocalTrainer.Melody.VoicePlot.Strokes(overlaySamples, 0, 2);
Check(strokes.Count == 3 && strokes[0].Count == 2 && strokes[1].Count == 2 && strokes[2].Count == 1,
    "trace breaks on silence and time gaps while preserving isolated observations");
var bounds = VocalTrainer.Melody.VoicePlot.PitchBounds([new(0, 60, 0, 2)], overlaySamples);
Check(bounds.Minimum <= 48 && bounds.Maximum >= 72, "octave errors remain visible outside the reference range");
Check(VocalTrainer.Melody.VoicePlot.Strokes(overlaySamples, .3, .6).Single().Count == 2,
    "trace viewport excludes observations outside the visible time range");
using (var viewportVm = new VocalTrainer.ViewModels.MelodyViewModel((_, _) => { }, () => { }, () => true,
    () => false, () => null, (_, _) => { }, (_, ex) => throw ex))
{
    viewportVm.SelectedTrack = new(0, "Long song", [new(60, 0, 120, 0)], 0);
    viewportVm.ViewStart = 70;
    Check(viewportVm.ViewStart == 70 && !viewportVm.FollowPlayhead && viewportVm.MaxViewStart == 100,
        "long songs can be inspected with a movable time window");
    viewportVm.FollowPlayhead = true;
    Check(viewportVm.ViewStart == 0, "auto-follow returns to the current playback position");
    viewportVm.Transpose = -24;
    viewportVm.SelectedTrack = boundaryTrack;
    Check(viewportVm.Transpose == 0 && viewportVm.Targets.Count == 2,
        "switching to an incompatible track resets transpose without leaving a stale score");
}
Console.WriteLine("All Windows build and MIDI regression tests passed.");

// Lyrics use source-MIDI seconds, independent of pitch and playback speed.
var lrc = VocalTrainer.Melody.LyricIO.ParseLrc("[ti:Тест]\n[00:00.000]Первая строка\n[00:02.5][00:05.00]Припев\n[00:06.000]\n");
Check(lrc.Count == 4 && lrc[1].Seconds == 2.5 && lrc[2].Text == "Припев", "LRC imports repeated timestamps and Cyrillic text");
Check(VocalTrainer.Melody.LyricIO.DecodeText([0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2]) == "Привет", "legacy Windows-1251 lyrics decode correctly");
Check(VocalTrainer.Melody.LyricIO.At(lrc, 2.499) == 0 && VocalTrainer.Melody.LyricIO.At(lrc, 2.5) == 1 &&
    VocalTrainer.Melody.LyricIO.At(lrc, -1) == -1, "lyric boundaries choose the current line without highlighting before its start");
Check(VocalTrainer.Melody.LyricIO.ParseLrc(VocalTrainer.Melody.LyricIO.ToLrc(lrc)).SequenceEqual(lrc), "LRC save and reload preserve timings and pause markers");
Rejects<FormatException>(() => VocalTrainer.Melody.LyricIO.ParseLrc("[00:61]Ошибка"), "invalid LRC seconds rejected");
Rejects<FormatException>(() => VocalTrainer.Melody.LyricIO.ParseLrc("[00:01]A\n[00:01]B"), "conflicting lyric timestamps rejected");
Rejects<FormatException>(() => VocalTrainer.Melody.LyricIO.Validate([new(2, "A"), new(1, "B")]), "manual lyric times must increase");

var rawLyricTrack = new List<byte>();
void WriteVariable(List<byte> destination, int value)
{
    var bytes = new List<byte> { (byte)(value & 127) };
    while ((value >>= 7) > 0) bytes.Insert(0, (byte)((value & 127) | 128));
    destination.AddRange(bytes);
}
void AddLyric(int delta, string text)
{
    WriteVariable(rawLyricTrack, delta); rawLyricTrack.AddRange([255, 5]);
    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
    WriteVariable(rawLyricTrack, bytes.Length); rawLyricTrack.AddRange(bytes);
}
AddLyric(0, "/Пер"); AddLyric(imported.TicksPerQuarter, "вая");
AddLyric(imported.TicksPerQuarter * 3, "\nВторая"); AddLyric(imported.TicksPerQuarter, " строка");
rawLyricTrack.AddRange([0, 255, 47, 0]);
var midiWithLyrics = File.ReadAllBytes(midiPath).ToList();
int oldTrackCount = (midiWithLyrics[10] << 8) | midiWithLyrics[11];
midiWithLyrics[10] = (byte)((oldTrackCount + 1) >> 8); midiWithLyrics[11] = (byte)(oldTrackCount + 1);
midiWithLyrics.AddRange(System.Text.Encoding.ASCII.GetBytes("MTrk"));
var trackLength = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(trackLength, rawLyricTrack.Count);
midiWithLyrics.AddRange(trackLength); midiWithLyrics.AddRange(rawLyricTrack);
var lyricalSong = VocalTrainer.Melody.MidiSongReader.ReadBytes(midiWithLyrics.ToArray());
Check(lyricalSong.Tracks.Count == imported.Tracks.Count && lyricalSong.LyricTracks.Count == 1,
    "MIDI lyric-only track survives import without becoming a note track");
Check(lyricalSong.LyricTracks[0].Lines[0].Text == "Первая" && lyricalSong.LyricTracks[0].Lines[1] == new VocalTrainer.Melody.LyricCue(2, "Вторая строка"),
    "MIDI syllables combine into lines using the global tempo map");

using (var lyricMelody = new VocalTrainer.ViewModels.MelodyViewModel((_, _) => { }, () => { }, () => true,
    () => false, () => null, (_, _) => { }, (_, ex) => throw ex))
{
    lyricMelody.SelectedTrack = new(0, "Lyrics", [new(60, 0, 10, 0)], 0);
    lyricMelody.Lyrics.LoadText("[00:00]Первая\n[00:02]Вторая\n[00:04]Третья", true, "Тест");
    lyricMelody.FragmentFrom = "2"; lyricMelody.FragmentTo = "6"; lyricMelody.SpeedPercent = 50;
    Check(lyricMelody.Lyrics.CurrentLine == "Вторая" && lyricMelody.Duration == 8,
        "lyric highlight starts at source fragment time at half speed");
    lyricMelody.Transpose = 3;
    Check(lyricMelody.Lyrics.CurrentLine == "Вторая", "transpose does not change lyric timing");
    lyricMelody.Lyrics.SelectedRow = lyricMelody.Lyrics.Rows[2]; lyricMelody.Lyrics.PracticeLineCommand.Execute(null);
    Check(lyricMelody.FragmentFrom == "4" && lyricMelody.FragmentTo == "10" && lyricMelody.Lyrics.CurrentLine == "Третья",
        "selected lyric line defines a practice fragment in original MIDI time");
    lyricMelody.Lyrics.LoadText("Одна\nДве", false, "TXT");
    Check(lyricMelody.Lyrics.Cues.Count == 0 && lyricMelody.Lyrics.HasUnappliedChanges, "TXT gets no guessed timings");
    lyricMelody.Lyrics.Rows[0].TimeText = "0"; lyricMelody.Lyrics.Rows[1].TimeText = "2,5";
    lyricMelody.Lyrics.ApplyCommand.Execute(null); lyricMelody.Lyrics.Highlight(2.5);
    Check(lyricMelody.Lyrics.CurrentLine == "Две" && lyricMelody.Lyrics.Rows[1].IsCurrent, "manual timing activates line highlighting");
    var previousLyrics = lyricMelody.Lyrics.Cues;
    lyricMelody.Lyrics.LoadText("Некорректный LRC", true, "Bad");
    Check(ReferenceEquals(previousLyrics, lyricMelody.Lyrics.Cues), "failed lyric import preserves the applied text");
}

var libraryFolder = Path.Combine(Path.GetTempPath(), "vocal-library-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(libraryFolder);
    string documentPath = Path.Combine(libraryFolder, "exercise.json");
    File.WriteAllText(documentPath, System.Text.Json.JsonSerializer.Serialize(exercise, ExerciseIO.Options));
    string indexPath = Path.Combine(libraryFolder, "library-index.txt");
    var library = new ExerciseLibrary(indexPath); library.Add(exercise, documentPath, true); library.Add(exercise, documentPath, true);
    Check(library.Entries.Count == 1, "reimporting an exercise updates its dropdown entry without duplication");
    var reopened = new ExerciseLibrary(indexPath); reopened.Load();
    Check(reopened.Entries.Count == 1 && reopened.Entries[0].Document.Title == exercise.Title, "exercise dropdown library survives restart");
    File.Delete(documentPath); var unavailable = new ExerciseLibrary(indexPath); unavailable.Load();
    Check(unavailable.Entries.Count == 0, "missing exercise files do not break startup");
    double markerPosition = 1.25;
    string savedLrc = Path.Combine(libraryFolder, "saved.lrc");
    var lyricsEditor = new VocalTrainer.ViewModels.LyricsViewModel(() => true, () => true, () => markerPosition, () => 10,
        (_, _) => { }, () => null, () => savedLrc);
    lyricsEditor.LoadText("Строка А\nСтрока Б", false, "Manual");
    lyricsEditor.MarkCommand.Execute(null); markerPosition = 3.5; lyricsEditor.MarkCommand.Execute(null);
    lyricsEditor.ApplyCommand.Execute(null); lyricsEditor.SaveCommand.Execute(null);
    Check(VocalTrainer.Melody.LyricIO.ParseLrc(File.ReadAllText(savedLrc)).Select(l => l.Seconds).SequenceEqual(new[] { 1.25, 3.5 }),
        "mark-current-time workflow saves reusable LRC timings");
    string lyricalMidiFile = Path.Combine(libraryFolder, "lyrics.mid");
    File.WriteAllBytes(lyricalMidiFile, midiWithLyrics.ToArray());
    using var autoLyrics = new VocalTrainer.ViewModels.MelodyViewModel((_, _) => { }, () => { }, () => true,
        () => false, () => lyricalMidiFile, (_, _) => { }, (_, ex) => throw ex);
    autoLyrics.ImportCommand.Execute(null);
    Check(autoLyrics.Lyrics.Sources.Count == 1 && autoLyrics.Lyrics.CurrentLine == "Первая",
        "importing a MIDI automatically selects lyrics from a separate text-only track");
}
finally { if (Directory.Exists(libraryFolder)) Directory.Delete(libraryFolder, true); }
Console.WriteLine("Lyrics and exercise library tests passed.");

var songTestRoot = Path.Combine(Path.GetTempPath(), "vocal-songs-" + Guid.NewGuid().ToString("N"));
try
{
    string sources = Path.Combine(songTestRoot, "sources"); Directory.CreateDirectory(sources);
    string firstMidi = Path.Combine(sources, "song.mid"), secondMidi = Path.Combine(sources, "other.mid");
    File.Copy(midiPath, firstMidi); File.WriteAllBytes(secondMidi, midiWithLyrics.ToArray());
    string textPath = Path.Combine(sources, "words.lrc");
    File.WriteAllText(textPath, "[00:00]Своя первая строка\n[00:02]Своя вторая строка");
    string songFolder = Path.Combine(songTestRoot, "Songs");
    var songStore = new VocalTrainer.Melody.SongLibrary(songFolder);
    string chosenMidi = firstMidi, chosenText = textPath;
    using (var songs = new MelodyViewModel((_, _) => { }, () => { }, () => true, () => false,
        () => chosenMidi, (_, _) => { }, (_, ex) => throw ex, () => chosenText, songLibrary: songStore))
    {
        songs.ImportCommand.Execute(null);
        var first = songs.SelectedSong!;
        Check(songs.Songs.Count == 1 && File.Exists(songStore.MidiPath(first)), "MIDI import keeps a managed copy in the song library");
        songs.Lyrics.ImportCommand.Execute(null);
        var state = songStore.Read(first);
        Check(state.TextFile is not null && File.ReadAllBytes(Path.Combine(songFolder, first.Id, state.TextFile)).SequenceEqual(File.ReadAllBytes(textPath)),
            "lyrics import copies the original text file and associates it with the MIDI");
        songs.Lyrics.Rows[1].TimeText = "2.5"; songs.Lyrics.ApplyCommand.Execute(null);
        songs.Transpose = 5;
        chosenMidi = secondMidi; songs.ImportCommand.Execute(null);
        Check(songs.Transpose == 0 && songs.TransposeText == "0" && songs.Targets[0].Midi == songs.SelectedTrack!.Notes[0].Midi,
            "opening a different MIDI resets transpose and its input to original pitch");
        Check(songs.Songs.Count == 2 && songs.Lyrics.CurrentLine == "Первая", "new MIDI does not inherit another song's external lyrics");
        songs.Lyrics.PlainText = "Новый текст\nВторая"; songs.Lyrics.FromPlainTextCommand.Execute(null);
        songs.Lyrics.Rows[0].TimeText = "0";
        songs.Lyrics.Rows[1].Text = "Черновик второй строки";
        songs.SelectedSong = first;
        Check(songs.Lyrics.Cues[1].Seconds == 2.5 && songs.Lyrics.CurrentLine == "Своя первая строка",
            "switching songs restores their own lyrics and edited timings");
        chosenMidi = firstMidi; songs.ImportCommand.Execute(null);
        Check(songs.Songs.Count == 2 && songs.Lyrics.Cues[1].Seconds == 2.5,
            "reimporting identical MIDI avoids duplicates and preserves associated lyrics");
        songs.SelectedSong = songs.Songs.Single(s => s != first);
    }
    // External files can disappear after import; the library remains self sufficient.
    File.Delete(firstMidi); File.Delete(secondMidi); File.Delete(textPath);
    using (var reopenedSongs = new MelodyViewModel((_, _) => { }, () => { }, () => true, () => false,
        () => null, (_, _) => { }, (_, ex) => throw ex, songLibrary: new(songFolder)))
    {
        Check(reopenedSongs.Songs.Count == 2 && reopenedSongs.SelectedSong?.Title == "other.mid", "song list and last selection survive restart without source files");
        Check(reopenedSongs.Lyrics.HasUnappliedChanges && reopenedSongs.Lyrics.Rows[1].Text == "Черновик второй строки" &&
            reopenedSongs.Lyrics.PlainText.Contains("Новый текст"), "unfinished lyric timings and pasted text survive restart");
        reopenedSongs.SelectedSong = reopenedSongs.Songs.Single(s => s.Title == "song.mid");
        Check(reopenedSongs.Lyrics.Cues[1].Seconds == 2.5 && reopenedSongs.Lyrics.Rows.Count == 2,
            "saved lyric mapping survives restart and deletion of original MIDI and LRC");
        reopenedSongs.Lyrics.LoadText("broken LRC", true, "bad");
        var checkStore = new VocalTrainer.Melody.SongLibrary(songFolder);
        Check(checkStore.Read(reopenedSongs.SelectedSong!).Lyrics!.Cues[1].Seconds == 2.5, "invalid lyric import preserves saved mapping");
        reopenedSongs.Lyrics.ClearCommand.Execute(null);
    }
    using (var clearedSongs = new MelodyViewModel((_, _) => { }, () => { }, () => true, () => false,
        () => null, (_, _) => { }, (_, ex) => throw ex, songLibrary: new(songFolder)))
        Check(clearedSongs.Lyrics.Rows.Count == 0 && clearedSongs.Lyrics.Cues.Count == 0, "removing song lyrics persists across restart");
    var validStore = new VocalTrainer.Melody.SongLibrary(songFolder); validStore.Load();
    File.WriteAllText(Path.Combine(songFolder, validStore.Entries[0].Id, "song.json"), "{ broken");
    var partiallyCorrupt = new VocalTrainer.Melody.SongLibrary(songFolder); partiallyCorrupt.Load();
    Check(partiallyCorrupt.Entries.Count == 1 && partiallyCorrupt.Warning is not null, "one corrupt song does not prevent loading the rest of the library");
}
finally { if (Directory.Exists(songTestRoot)) Directory.Delete(songTestRoot, true); }
Console.WriteLine("Persistent song and lyric library checks passed.");
using (var loopingLyrics = new VocalTrainer.ViewModels.MelodyViewModel((_, _) => { }, () => { }, () => true,
    () => false, () => null, (_, _) => { }, (_, ex) => throw ex))
{
    loopingLyrics.SelectedTrack = new(0, "Loop lyrics", [new(60, 0, .8, 0)], 0);
    loopingLyrics.LoopCount = 2;
    loopingLyrics.Lyrics.LoadText("[00:00]Начало\n[00:00.400]Конец", true, "Loop");
    loopingLyrics.PreviewCommand.Execute(null);
    var lyricLoopWatch = System.Diagnostics.Stopwatch.StartNew();
    while (loopingLyrics.Cursor < .45 && lyricLoopWatch.Elapsed.TotalSeconds < 2) await Task.Delay(10);
    Check(loopingLyrics.Lyrics.CurrentLine == "Конец", "lyrics advance during reference playback");
    loopingLyrics.TogglePlaybackPause();
    double pausedSource = loopingLyrics.SourceSeconds; await Task.Delay(100);
    Check(Math.Abs(loopingLyrics.SourceSeconds - pausedSource) < .001 && loopingLyrics.Lyrics.CurrentLine == "Конец", "preview pause freezes lyric source time");
    loopingLyrics.TogglePlaybackPause();
    while (!loopingLyrics.DisplayedLoop.StartsWith("Повтор 2/") && lyricLoopWatch.Elapsed.TotalSeconds < 3) await Task.Delay(10);
    Check(loopingLyrics.DisplayedLoop.StartsWith("Повтор 2/") && loopingLyrics.Lyrics.CurrentLine == "Начало", "lyrics restart with the repeated MIDI fragment");
    loopingLyrics.Stop();
    while (loopingLyrics.IsBusy && lyricLoopWatch.Elapsed.TotalSeconds < 4) await Task.Delay(10);
}

var duetTones = new System.Collections.Concurrent.ConcurrentQueue<(double Hz, double Seconds)>();
int duetSounding = 0;
var duetErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
using (var duet = new VocalTrainer.ViewModels.MelodyViewModel((hz, seconds) => { duetTones.Enqueue((hz, seconds)); Interlocked.Exchange(ref duetSounding, 1); },
    () => Interlocked.Exchange(ref duetSounding, 0), () => true, () => true, () => null, (_, _) => { }, (_, ex) => duetErrors.Enqueue(ex)))
{
    duet.SelectedTrack = new(0, "Two octaves", [new(60, 0, 2, 0), new(72, 2, 2, 0)], 0);
    Check(duet.ReferencePitches.Count == 2 && duet.ReferencePitches[0].Octave.Contains("1-я") && duet.ReferencePitches[1].Octave.Contains("2-я"),
        "reference analysis identifies notes and scientific octaves");
    duet.Transpose = -12; duet.SpeedPercent = 50;
    Check(duet.ReferencePitches[0].Note == "C3" && Math.Abs(duet.ReferencePitches[0].FrequencyHz - NoteCatalog.ToFrequency(48)) < .001 && duet.ReferencePitches[0].Seconds == 4,
        "reference analysis follows transpose and time stretching");
    duet.SpeedPercent = 100;
    duet.SelectedTrack = new(0, "Duet", [new(60, 0, .18, 0), new(62, .38, .18, 0)], 0);
    duet.LoopCount = 2; duet.SingWithReference = true;
    duet.PracticeCommand.Execute(null);
    var duetWatch = System.Diagnostics.Stopwatch.StartNew();
    while (duetTones.IsEmpty && duetWatch.Elapsed.TotalSeconds < 6) await Task.Delay(5);
    Check(duet.IsPracticing && !duetTones.IsEmpty && Math.Abs(duetTones.First().Hz - NoteCatalog.ToFrequency(48)) < .001,
        "sing-with-reference starts the transposed tone after count-in");
    duet.SingWithReference = false;
    Check(duet.SingWithReference, "reference mode cannot change during a take");
    duet.TogglePlaybackPause(); int pausedToneCount = duetTones.Count;
    await Task.Delay(80);
    Check(duet.IsPaused && duetTones.Count == pausedToneCount && Volatile.Read(ref duetSounding) == 0,
        "duet pause silences the reference and freezes playback");
    duet.TogglePlaybackPause();
    while (duetTones.Count == pausedToneCount && duetWatch.Elapsed.TotalSeconds < 7) await Task.Delay(5);
    Check(duetTones.Count > pausedToneCount, "duet resume recreates the current reference tone");
    while (duet.IsPracticing && duetWatch.Elapsed.TotalSeconds < 8) await Task.Delay(10);
    Check(!duet.IsBusy && duetErrors.IsEmpty && Volatile.Read(ref duetSounding) == 0,
        "duet timeline completes and releases audio without microphone callbacks");
    Check(duetTones.Count(t => Math.Abs(t.Hz - NoteCatalog.ToFrequency(48)) < .001) >= 2 &&
        duetTones.Count(t => Math.Abs(t.Hz - NoteCatalog.ToFrequency(50)) < .001) >= 2 && duetTones.All(t => t.Seconds <= .18 + .001),
        "duet preserves note durations, rests and repeats");
}

var freeAnalysis = new FreeSingingAnalysis();
var freeFirst = freeAnalysis.Analyze(new(NoteCatalog.ToFrequency(60) * Math.Pow(2, .12 / 12), .1, .99), 0);
Check(freeFirst.Midi == 60 && Math.Abs(freeFirst.DeviationCents!.Value - 12) < .001, "free singing measures cents against the nearest note");
var freeUp = freeAnalysis.Analyze(new(NoteCatalog.ToFrequency(62), .1, .99), 120);
Check(freeUp.Direction == "↑ Вверх" && freeUp.MovementCents > 180, "free singing detects upward movement");
var freeDown = freeAnalysis.Analyze(new(NoteCatalog.ToFrequency(60), .1, .99), 240);
Check(freeDown.Direction == "↓ Вниз", "free singing detects downward movement");
Check(!freeAnalysis.Analyze(new(null, .001, 0), 360).Reliable &&
    freeAnalysis.Analyze(new(NoteCatalog.ToFrequency(60), .1, .99), 480).MovementCents is null,
    "free singing silence breaks the movement sequence");
Check(!freeAnalysis.Analyze(new(double.NaN, .1, .99), 600).Reliable &&
    !freeAnalysis.Analyze(new(440, .1, .2), 720).Reliable, "free singing excludes invalid and low-confidence tones");
var freeView = new FreeSingingViewModel();
for (int i = 0; i <= 300; i++) freeView.Observe(new(NoteCatalog.ToFrequency(60), .1, .99), i * 120);
Check(freeView.Voice.Count <= 251 && freeView.Voice[0].Seconds >= 6 && freeView.Note == "C4",
    "free pitch history keeps a moving thirty-second window");

var freeLogFolder = Path.Combine(Path.GetTempPath(), "vocal-free-" + Guid.NewGuid().ToString("N"));
try
{
    using var freeLog = new SessionLogger(freeLogFolder, 48, "Synthetic");
    freeLog.SetPhase("free_singing");
    var frame = new FreeSingingAnalysis().Analyze(new(440, .1, .99), 0);
    freeLog.FreeSample(frame, new(440, .1, .99));
    Check(freeLog.Finish().Contains("Свободное пение"), "session summary includes free singing accuracy");
    var events = File.ReadAllLines(freeLog.FilePath).Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
    try
    {
        var sample = events.Single(e => e.RootElement.GetProperty("type").GetString() == "free_pitch_sample").RootElement.GetProperty("details");
        Check(sample.GetProperty("Midi").GetInt32() == 69 && sample.GetProperty("DeviationCents").GetDouble() == 0 &&
            sample.GetProperty("Direction").GetString() == "Новый звук", "free JSONL records the note, octave, cents and movement");
    }
    finally { foreach (var item in events) item.Dispose(); }
    Check(Directory.GetFiles(freeLogFolder, "*.wav").Length == 0, "free singing logging does not record audio");
}
finally { if (Directory.Exists(freeLogFolder)) Directory.Delete(freeLogFolder, true); }

var seekClock = new PausableClock(); seekClock.Pause(); seekClock.Seek(5); await Task.Delay(40);
Check(seekClock.IsPaused && seekClock.ElapsedSeconds == 5, "seeking preserves a paused logical clock");
seekClock.Resume(); await Task.Delay(30);
Check(seekClock.ElapsedSeconds >= 5.02, "a seeked clock resumes from the chosen position"); seekClock.Stop();

var seekTones = new System.Collections.Concurrent.ConcurrentQueue<double>();
var seekEvents = new System.Collections.Concurrent.ConcurrentQueue<string>();
using (var seekMelody = new MelodyViewModel((hz, _) => seekTones.Enqueue(hz), () => { }, () => true,
    () => true, () => null, (kind, _) => seekEvents.Enqueue(kind), (_, ex) => throw ex))
{
    seekMelody.SelectedTrack = new(0, "Seek", [new(60, 0, 2, 0), new(62, 2, 2, 0), new(64, 4, 4, 0)], 0);
    seekMelody.Lyrics.LoadText("[00:00]A\n[00:02]B\n[00:04]C", true, "Seek");
    seekMelody.Seek(3);
    Check(seekMelody.Cursor == 3 && seekMelody.Lyrics.CurrentLine == "B", "idle seek updates cursor and lyrics");
    seekMelody.PreviewCommand.Execute(null);
    Check(seekTones.TryPeek(out double firstTone) && Math.Abs(firstTone - NoteCatalog.ToFrequency(62)) < .001,
        "reference playback starts at the clicked position");
    seekMelody.TogglePlaybackPause(); seekMelody.Seek(5); int toneCount = seekTones.Count; await Task.Delay(60);
    Check(seekMelody.IsPaused && seekMelody.Cursor == 5 && seekMelody.Lyrics.CurrentLine == "C" && seekTones.Count == toneCount,
        "seeking a paused reference changes position without playing");
    seekMelody.TogglePlaybackPause(); await Task.Delay(60);
    Check(seekTones.Any(hz => Math.Abs(hz - NoteCatalog.ToFrequency(64)) < .001), "resumed reference uses the note at the seek destination");
    seekMelody.Stop(); var seekWatch = System.Diagnostics.Stopwatch.StartNew();
    while (seekMelody.IsBusy && seekWatch.Elapsed.TotalSeconds < 3) await Task.Delay(10);
    seekMelody.Seek(.5); seekMelody.PracticeCommand.Execute(null); seekWatch.Restart();
    while (!seekMelody.IsPracticing && seekWatch.Elapsed.TotalSeconds < 6) await Task.Delay(15);
    Check(seekMelody.IsPracticing && seekMelody.Cursor >= .5, "practice can start from a selected position");
    for (int i = 0; i < 4; i++) seekMelody.OnMicrophoneSample(NoteCatalog.ToFrequency(60), .99);
    seekMelody.TogglePlaybackPause(); seekMelody.Seek(5);
    Check(seekMelody.Voice.Count == 0 && seekMelody.IsPaused, "practice seek clears the old take and preserves pause");
    seekMelody.TogglePlaybackPause();
    for (int i = 0; i < 4; i++) seekMelody.OnMicrophoneSample(NoteCatalog.ToFrequency(64), .99);
    seekMelody.Stop();
    Check(seekMelody.Summary.Contains("1/1") && seekMelody.Summary.Contains("последнего перехода") && seekEvents.Contains("midi_seek"),
        "seek scoring counts only the new observed segment and records navigation");
}
Console.WriteLine("Free singing and timeline seek checks passed.");
