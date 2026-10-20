# HeartRateMonitor 💓

**Your live heart rate from a Bluetooth chest strap, as a big always-on-top number over your game.**

<p>
  <img src="docs/overlay.png" alt="The overlay: a beating heart, the BPM in zone color, zone name and a live graph" height="240">
  <img src="docs/main-window.png" alt="The main window with the full-session graph over colored heart rate zones" height="240">
</p>

## Download

**[⬇ HeartRateMonitor.exe (latest)](https://github.com/RelentlessOldMan/HeartRateMonitor/releases/latest/download/HeartRateMonitor.exe)**: one standalone file for Windows 10/11, with no .NET install needed. Other versions and a `.zip` are on the [releases page](https://github.com/RelentlessOldMan/HeartRateMonitor/releases).

The exe isn't code-signed, so Windows SmartScreen may warn the first time: click **More info → Run anyway**.

## Why

I wanted to see my heart rate while playing a dance game: big, readable and in a color I pick, without alt-tabbing to a phone app.

## Usage

1. Put the strap on (wet the sensor pads) and disconnect it from your phone or watch. Most straps only talk to one device at a time.
2. Run `HeartRateMonitor.exe`. It connects to the first heart rate strap it finds and remembers it from then on. You don't need to pair it in Windows.
3. Drag the overlay where you want it and drag its corner to resize it. Then tick **Lock overlay** so clicks go straight through to the game.

Run your game in **borderless/windowed** mode. Exclusive fullscreen draws over every other window, including always-on-top ones.

**Features**
- Overlay: the number scales with the window. You can choose the font, a fixed color or color by zone, a glow outline, a background color with any opacity, a heart that beats at your actual BPM, the zone name and a live graph of the last 1–30 minutes.
- Two heart rates: the strap's own number and one calculated from the beat-to-beat (RR) intervals, which is more trustworthy when the strap's number looks off. Show either or both, separately for the main window and the overlay. With both, the graph draws the calculated line in cyan.
- Heart rate zones: generated from your max heart rate (50/60/70/80/90%) or fully custom names, thresholds and colors.
- Daily stats: time in each zone today, kept across restarts. Stored per BPM, so editing your zones re-sorts the whole day.
- Recording: starts automatically when the app opens. It's written as you go to a CSV (timestamp, BPM, RR intervals, skin contact, calculated BPM) in a scratch folder. **Save recording** moves it to your recordings folder, optionally with a `.tcx` you can upload to Strava or Garmin Connect, and stops recording; **Start recording** begins a new one. **Reset** clears the graph and, if a recording is going, throws it away and starts a fresh one. Closing the app asks whether to save. If the app crashes, the recording is recovered into the recordings folder on the next start.
- Events from other programs: games and tools can report songs, rounds, sets or anything else by dropping small JSON files in a folder (see below). The **Events** tab and the graph show each one with your average, max and min heart rate, and they're saved with the recording.
- Recordings tab: open a saved recording and see it just like the live view: the same stats, graph and events list. Click a song (or a stream inside it) or drag across the graph to select a stretch of time, and the stats for it show up. **Cut selection** removes that time, plus any song that began inside it, so a false start can go. Times after a cut keep their real clock times; the graph shows a gap. **Undo** steps back. **Save** writes over the recording, and the first time a recording is edited its original files are copied to a `backup` folder next to it.
- Reconnects automatically if the strap drops out. Settings are saved as you change them.

**Command line**
- `--dummy`: fake heart rate data, for trying it out without a strap.
- `--snapshot <folder>`: renders every screen to PNGs off-screen and exits (used for the screenshots above).

Tested with a Coospo H808S. Any strap that speaks the standard Bluetooth LE Heart Rate profile should work (Polar, Garmin HRM-Dual, Wahoo TICKR, …).

## Events from other programs

Any program can tell the app what you're doing, so your heart rate can later be broken down by song, level, set or whatever else. The bundled use is StepMania/ITGmania with a Simply Love module that reports each song, its chart and difficulty, the result and pauses. Nothing in the format is specific to it.

**Channel:** write one JSON object per `.json` file into a watched folder. The app checks about twice a second, reads files in **filename order**, and deletes each one once it's read. Watched folders:
- `%AppData%\HeartRateMonitor\inbox` (always)
- whatever is listed in Settings → *Events from other programs*. The defaults are `%AppData%\StepMania 5.1\Save\HeartRate\events` and `%AppData%\ITGmania\Save\HeartRate\events`.

Name files so they sort in order, for example `<session-stamp>-<sequence number padded to 6 digits>.json`. Writing in place is fine: a file that doesn't parse yet is retried. One that still isn't valid after 10 seconds is moved into a `bad` subfolder and noted in the log. Other file types are ignored. The event's time is when the file was created, so write it when the thing happens.

**Format:** only `kind` is required.

```jsonc
{
  "v": 1,
  "kind": "begin",               // "begin" | "end" | "mark"
  "type": "song",                // what it is: "song", "course", "set", "round", "pause", … (default "event")
  "id": "20261018-201500-000001",// pairs a begin with its end; marks and children point at it with "parent"
  "parent": "…",                 // optional: id of the span this one is inside (a song in a course)
  "label": "Acme Songo Wongo [Hard 7]", // what to show
  "source": "simply-love",       // who sent it
  "data": { … }                  // anything you like; stored as-is
}
```

- `begin`/`end` mark out a span. An `end` is matched to the open span with the same `id`, or failing that the latest open one with the same `type` and `source`. A `begin` with no `end` is closed when the next span of the same type and source begins.
- `end` with `data.result` (e.g. `"cleared"`, `"failed"`, `"quit"`) shows in the Result column. `data.late: true` means the time is unreliable: the span is ended at its last mark instead, or after `data.length_s` seconds if it has no marks.
- `mark` is a single moment (a pause, a screen change). Give it a `parent` to attach it to a span.
- A `begin` can list the parts of what it starts in `data.segments`: `[{"kind": "stream" | "break", "start_s": 12.5, "end_s": 40.1, "measures": 16}, …]`, in seconds of the song's own time, with the begin itself at `music_seconds` (top level, 0 if left out). Each becomes a child span (e.g. "Stream 2") with its own heart rate stats; streams are shaded lighter in the graph. Parts after a song ends early (a fail) are dropped.

**Saved files** (next to the recording's CSV):
- `HeartRate_<time>.events.jsonl`: every event exactly as received, one per line, plus `"received"`, the time the app gave it.
- `HeartRate_<time>.spans.csv`: one row per span with start, end, duration, result, avg/min/max BPM, and title, artist, pack, difficulty and meter when the sender included them (`data.title` and the like, and `data.players[0]`), plus the full `data` as JSON.

## How it works

The strap broadcasts the standard Bluetooth LE Heart Rate service (`0x180D`). The app scans for its advertisement, connects through the Windows WinRT Bluetooth APIs and subscribes to the Heart Rate Measurement characteristic (`0x2A37`), which sends BPM plus beat-to-beat RR intervals about once a second. It also reads battery level when the strap offers it. The overlay is a transparent, topmost WPF window. Locking it adds `WS_EX_TRANSPARENT` so mouse input passes through.

## Layout

```
HeartRateMonitor/
  Sensors/      BLE strap connection, measurement parser, dummy data source
  Recording/    CSV + TCX session recorder
  Events/       Event folder inbox, begin/end span pairing
  Controls/     Zone-colored graph, the stats/graph/events views shared by the live and Recordings tabs, converters
  Models/       Settings (JSON in %AppData%\HeartRateMonitor), zones, samples
  Session.cs           Samples, events, spans and stats for one session (live or opened from a file)
  RunViewer.cs         The Recordings tab: open, cut, undo, save with backup
  OverlayWindow.xaml   The always-on-top display
  MainWindow.xaml      Connection, overlay options, zones, recording
  Snapshot.cs          Off-screen rendering for --snapshot
.github/workflows/release.yml   Bump <Version> in the csproj and push to get a new release
```

## Contributing

This is a personal tool, published as-is, and doesn't take issues or pull requests. Fork it and make it your own. 💓

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.
