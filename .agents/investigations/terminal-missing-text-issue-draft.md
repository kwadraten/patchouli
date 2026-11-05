# Issue draft: stale captured frame after output following ESU in the same PTY chunk

建议提交到 `tomlm/Iciclecreek.Avalonia.Terminal`。以下为可直接复制的英文草稿；本次没有发布 issue。

## Title

Output after ESU in the same PTY chunk can remain invisible because a stale capture has the current generation

## Description

A captured frame taken at ESU can be treated as current even though the rest of the same PTY chunk changes the buffer afterwards. If the chunk-tail capture is throttled, the renderer selects that stale frame instead of the final buffer contents.

I reproduced the frame-selection problem with the published NuGet packages:

- Iciclecreek.Avalonia.Terminal 4.1.0 / XTerm.NET 3.0.0
- Iciclecreek.Avalonia.Terminal 4.1.3 / XTerm.NET 3.0.2
- Windows, .NET 10

The original report was disappearing Chinese text while typing in an embedded OpenCode terminal. That application's raw PTY stream has not been captured, so I cannot yet attribute the original screenshot to this defect. The reproduction below isolates a separate, confirmed stale-frame selection issue. It does not require an IME, and the same mechanism applies to ASCII.

## Trigger

A recent chunk-boundary capture must have set `_lastChunkPublish` less than one `ChunkPublishIntervalTicks` ago (approximately 33.3 ms). Then a single PTY read delivers this sequence:

```text
ESC[?2026h ESC[1;1H ESC[2K ESC[?2026l HELLO
```

Spaces above separate operations for readability; the actual bytes contain no separator spaces:

```csharp
"\u001b[?2026h\u001b[1;1H\u001b[2K\u001b[?2026lHELLO"
```

This opens synchronized output, clears row 1, ends synchronized output, and then writes `HELLO` outside the synchronized region but in the same read chunk. PTY read boundaries do not necessarily match application write or frame boundaries.

## Execution path

Source links below are pinned to the 4.1.3 package's repository commit `bbee5ed1f80cf00388749523d3389e5f30c41a1b`.

1. [`TerminalView.Pty.cs`, `ConsumeOutputChunk`](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/bbee5ed1f80cf00388749523d3389e5f30c41a1b/src/Iciclecreek.Avalonia.TerminalWindow/TerminalView.Pty.cs#L571) increments `_liveWriteGeneration` once for the entire chunk. Call this generation `G`.
2. The same method calls `_terminal.Write(chunk.Span)` at line 591. Parsing starts synchronized output and clears the row.
3. ESU fires [`OnSynchronizedOutputChanged`](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/bbee5ed1f80cf00388749523d3389e5f30c41a1b/src/Iciclecreek.Avalonia.TerminalWindow/TerminalView.cs#L1980). Before ending the atomic update, the handler publishes a snapshot with generation `G`. At this point row 1 is blank.
4. Parsing continues inside the same `_terminal.Write`: `HELLO` is written to the live buffer. `_liveWriteGeneration` is still `G`.
5. When `Write` returns, `_bufferWriteInProgress` becomes false. `_atomicUpdate` is also false, so the chunk tail calls `PublishThrottled` at line 619.
6. [`FrameCapturePool.PublishThrottled`](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/bbee5ed1f80cf00388749523d3389e5f30c41a1b/src/Iciclecreek.Avalonia.TerminalWindow/FrameCapture.cs#L171) returns early because of the recent chunk-boundary capture. The snapshot published at ESU remains the published frame.
7. [`TerminalView.Render`](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/bbee5ed1f80cf00388749523d3389e5f30c41a1b/src/Iciclecreek.Avalonia.TerminalWindow/TerminalView.Render.cs#L376) calls `PinForRender` with live generation `G`, `atomicUpdate=false`, `writeInProgress=false`.
8. [`PinForRender`](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/bbee5ed1f80cf00388749523d3389e5f30c41a1b/src/Iciclecreek.Avalonia.TerminalWindow/FrameCapture.cs#L203) accepts the blank snapshot because its generation equals `G`.
9. At [`Render` line 449](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/bbee5ed1f80cf00388749523d3389e5f30c41a1b/src/Iciclecreek.Avalonia.TerminalWindow/TerminalView.Render.cs#L449), `capturedFrame?.LineAt(y)` wins over the live line. `HELLO` is therefore absent from the selected drawing input even though it exists in the emulator buffer.

The invariant that fails is: **a matching chunk generation does not imply that the snapshot contains all mutations of that chunk**. An ESU snapshot is taken inside parsing, before the entire chunk has completed.

## Minimal console reproduction

This probes the published DLL's real emulator and internal frame-capture implementation via reflection. It does not instantiate a window or simulate the entire PTY reader. The calls mirror the production capture order described above. A private timestamp is reset immediately before the tail call to deterministically select the throttled branch rather than depending on machine speed or debugger pauses.

Create a standalone console project:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Iciclecreek.Avalonia.Terminal" Version="4.1.3" />
  </ItemGroup>
</Project>
```

`Program.cs`:

```csharp
using System.Diagnostics;
using System.Reflection;
using XTerm;
using XTerm.Buffer;
using XTerm.Options;

using var terminal = new Terminal(new TerminalOptions { Cols = 80, Rows = 2 });
var assembly = Assembly.Load("Iciclecreek.Avalonia.Terminal");
var poolType = assembly.GetType("Iciclecreek.Terminal.FrameCapturePool", true)!;
var pool = Activator.CreateInstance(poolType, nonPublic: true)!;
var publish = poolType.GetMethod("Publish")!;
var publishThrottled = poolType.GetMethod("PublishThrottled")!;
var pin = poolType.GetMethod("PinForRender")!;
var lastChunkPublish = poolType.GetField("_lastChunkPublish",
    BindingFlags.Instance | BindingFlags.NonPublic)!;

const long generation = 1;
terminal.SynchronizedOutputChanged += (_, e) =>
{
    if (!e.Active)
        publish.Invoke(pool, new object[] { terminal, generation });
};

// A recent preceding chunk has primed the capture throttle.
publishThrottled.Invoke(pool, new object[] { terminal, 0L });

// One chunk, one generation. The ESU callback captures a blank first row;
// parsing continues and writes HELLO after that callback returns.
terminal.Write("\u001b[?2026h\u001b[1;1H\u001b[2K\u001b[?2026lHELLO");

// Force the ordinary "previous chunk was recent" timing condition.
lastChunkPublish.SetValue(pool, Stopwatch.GetTimestamp());
publishThrottled.Invoke(pool, new object[] { terminal, generation });

var frame = pin.Invoke(pool, new object[]
{
    0, 80, 2, generation, false, false
})!;
var selectedLine = (BufferLine)frame.GetType().GetMethod("LineAt")!
    .Invoke(frame, new object[] { 0 })!;
var live = terminal.GetVisibleLines()[0].TrimEnd();
var selected = selectedLine.TranslateToString(trimRight: true);

Console.WriteLine($"Live: '{live}'");
Console.WriteLine($"Selected capture: '{selected}'");
if (live != "HELLO" || selected != "")
    throw new Exception("The expected stale-frame condition was not reproduced.");
```

Run `dotnet run`. Actual output:

```text
Live: 'HELLO'
Selected capture: ''
```

Replacing `HELLO` with Chinese text produces the same mismatch. A separate natural-timing probe, without setting the private timestamp, also reproduced the mismatch in both versions.

## Expected behavior

Once the complete chunk has been parsed and no synchronized update is open, frame selection should not consider an intermediate ESU snapshot current if later bytes from the same chunk changed the buffer. A safe final capture or the final live state should be used.

## Persistence and recovery

Waiting beyond 33.3 ms is not sufficient by itself: `PublishThrottled` runs when output is consumed, not automatically when its interval expires. A subsequent paint can continue to accept the same-generation stale capture. `RequestPaint` requests an invalidation; it does not refresh the snapshot.

A subsequent accepted output chunk advances the generation, so the old capture should no longer pass the same-generation check when neither an atomic update nor a write is in progress; a later capture can also replace it. These are source-level recovery expectations, not a claim that every real TUI session recovers identically.

## Possible fix direction

Track whether the buffer changed after an in-chunk ESU capture, or otherwise ensure the final chunk state invalidates/replaces that intermediate capture even when the chunk-tail publisher is throttled. Raising FPS or adding paint invalidations alone does not address the freshness check.

Regression cases should include an ESU followed by printable text, cursor movement/erase operations after ESU, multiple synchronized regions within one chunk, a chunk ending with a synchronized region still open, and repainting after the throttle interval without additional output.
