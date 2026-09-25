# C# Concepts Used in This Project: async/await, records, and LINQ

This project leans heavily on three C# features that don't show up in a lot
of introductory material: **async/await**, **records**, and **LINQ**. This
guide explains each one from first principles, using real snippets pulled
from `Program.cs`, `EquilibriumDetector.cs`, and `ArduinoHeaterController.cs`
so you can cross-reference the explanation against the actual code.

Read `PROGRAM_GUIDE.md` for what the program *does*; read this file for the
C# *syntax* it's written in.

---

## Part 1: async / await

### The problem it solves

A lot of what this program does is **wait** — wait for the Arduino to reply
over serial, wait for the LR8400 to respond over the network, wait a fixed
number of seconds for relays to settle, wait a second before polling
temperature again. If you wrote this with ordinary blocking calls, the
single thread running your program would sit completely idle during every
one of those waits — not doing any work, just frozen.

`async`/`await` lets a method say "start this operation, and give control
back to whoever called me until it's done" instead of blocking. This matters
a lot here because it's how the program can, for example, keep the CLI
process responsive to Ctrl+C while it's in the middle of an
`await Task.Delay(...)`.

### The basic shape

A method that can be awaited is marked `async` and returns `Task` (nothing
meaningful to hand back) or `Task<T>` (hands back a `T` once it's done).
Here's the program's entry point:

```csharp
static async Task<int> RunAsync(string[] args)
{
    ...
    return 0;
}
```

- `async` tells the compiler "this method contains `await` and needs to be
  turned into a state machine that can pause and resume."
  `Task<int>` is what the caller actually gets back immediately — not the
  `int` itself, but a "promise" that an `int` will show up later.
- Calling it looks like this, right at the top of the file:
  ```csharp
  return await RunAsync(args);
  ```
  `await` is what "unwraps" the `Task<int>` into the actual `int`, pausing
  this point in the code until `RunAsync` finishes, without blocking the thread.

### Every `await` is a pause point, not a blocking wait

Look at this real sequence from the startup code:

```csharp
Console.WriteLine($"Opening Arduino on {options.ArduinoPort} at {options.ArduinoBaudRate} baud ...");
await heater.OpenAsync(cancellation.Token);
Console.WriteLine("Arduino connected; all heater outputs are OFF.");
```

Reading top to bottom, this looks like ordinary sequential code — and for
your purposes as a reader, it basically is: line 1 runs, then line 2 starts
and *eventually* finishes, then line 3 runs. The difference from a normal
(synchronous) method call is what happens underneath while `OpenAsync` is
waiting for the serial port: the calling thread isn't spinning uselessly, and
in an environment with more work to do (a UI, a web server, multiple
simultaneous tasks) it would be free to do that other work. In this simple
console program that mostly doesn't matter for performance — it matters here
because it lets the same thread keep checking for Ctrl+C between operations
(see the `CancellationToken` section below).

### `Task` vs `Task<T>`

You'll see both in this codebase:

```csharp
static async Task WaitForRecordingStateAsync(
    Lr8400Client logger, TimeSpan timeout, CancellationToken cancellationToken)
```
returns plain `Task` — it does something, but doesn't hand back a value.

```csharp
static async Task<TemperatureSnapshot> ReadSnapshotAsync(
    Lr8400Client logger, IReadOnlyList<string> channels, CancellationToken cancellationToken)
```
returns `Task<TemperatureSnapshot>` — awaiting it gives you back a
`TemperatureSnapshot`, exactly like the `RunAsync` example above gives you
back an `int`. This is used constantly in the main loop:

```csharp
TemperatureSnapshot snapshot = await ReadSnapshotAsync(logger, recordingChannels, cancellation.Token);
```

### `await using` — asynchronous cleanup

Near the top of `RunAsync`:

```csharp
await using Lr8400Client logger = new();
await using ArduinoHeaterController heater = new(options.ArduinoPort, options.ArduinoBaudRate);
```

Both `Lr8400Client` and `ArduinoHeaterController` implement
`IAsyncDisposable` (look at the bottom of `ArduinoHeaterController.cs` for
`public async ValueTask DisposeAsync()`), meaning cleaning them up itself
involves an asynchronous operation — in this case, sending an "all outputs
off" command and closing the serial port before returning. `await using`
is the async version of the ordinary C# `using` statement: it guarantees
`DisposeAsync()` is awaited automatically when `heater`/`logger` go out of
scope, whether the method returns normally or an exception is thrown partway
through.

### `CancellationToken` — how Ctrl+C actually stops things

`CancellationToken` is the standard .NET way to ask a running async
operation to stop cooperatively. This program creates one token and threads
it through almost every `await`:

```csharp
using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
    Console.WriteLine("Stopping safely; press Ctrl+C again only if an instrument is unresponsive.");
};
```

`cancellation.Cancel()` flips the shared token into a "cancelled" state.
Nothing stops immediately by magic — every loop has to actually check for
it, which is exactly what this line at the top of both `while (true)` loops
does:

```csharp
cancellation.Token.ThrowIfCancellationRequested();
```

If the token has been cancelled, this throws an `OperationCanceledException`
right there, which unwinds up to the `catch (OperationCanceledException)`
block near the bottom of `RunAsync` — and, critically, still passes through
the `finally` block that switches the heater off. Passing the same
`cancellation.Token` into methods like `Task.Delay(livePollInterval, cancellation.Token)`
means even a delay currently in progress gets interrupted rather than run to completion.

### try / catch / finally with async code

```csharp
try
{
    ... // connect to instruments, run every device, etc.
    return 0;
}
catch (OperationCanceledException)
{
    ...
    return 3;
}
catch (Exception exception)
{
    ...
    return 1;
}
finally
{
    if (heater.IsOpen) { await heater.AllOffAsync(CancellationToken.None); ... }
    ...
}
```

This behaves exactly like ordinary synchronous try/catch/finally — `await`
doesn't change the rules here. The important design point (not a language
feature, just how this program uses the language) is that the `finally`
block awaits `heater.AllOffAsync(CancellationToken.None)` using a *fresh*,
never-cancelled token, on purpose: even if the reason we got to `finally` is
that the user's cancellation token was triggered, the "turn the heater off"
command must not itself be cancelled.

---

## Part 2: Records

### What a record actually is

A `record` is a reference type (like a `class`) but with two big differences
switched on by default:

1. **Value-based equality** — two record instances with the same property
   values are considered equal, unlike ordinary classes where `==` compares
   references by default.
2. **Concise, immutable-by-default declaration** — you list the properties
   once in the type's header (called a *positional record*) instead of
   writing out a constructor and properties by hand.

Records are a natural fit here because so much of this program is passing
around plain bundles of data that describe "this is what happened" or "this
is how it's configured" — not objects with complex internal behaviour.

### A simple example from this codebase

From `EquilibriumDetector.cs`:

```csharp
internal sealed record TemperatureSnapshot(
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, double> Values);
```

This one line is equivalent to writing a class with a constructor taking
`(DateTimeOffset, IReadOnlyDictionary<string, double>)`, two get-only
properties `TimestampUtc` and `Values`, a working `Equals`/`GetHashCode`, and
a readable `ToString()` — all generated by the compiler. Creating one looks
exactly like calling a constructor:

```csharp
return new TemperatureSnapshot(DateTimeOffset.UtcNow, selected);
```

Another one, also from `EquilibriumDetector.cs`:

```csharp
internal sealed record EquilibriumResult(
    bool IsEquilibrium,
    TimeSpan ObservedWindow,
    double WorstSpanC,
    double WorstAbsoluteSlopeCPerMinute,
    string? LimitingChannel);
```

This is the return value of `EquilibriumDetector.Evaluate()` — five related
values bundled together so the caller can do things like
`equilibrium.IsEquilibrium` and `equilibrium.WorstSpanC` by name, instead of
juggling a tuple of five loose values.

### `DeviceRun` — a record with its own logic

Records aren't limited to plain data — you can still add methods and a
body, just like a class:

```csharp
file sealed record DeviceRun(
    string FolderName, string DisplayName, HeaterDevice Device, MonitorRelaySelection MonitorRelay)
{
    public static IReadOnlyList<DeviceRun> All { get; } =
    [
        new("01_UU_IGBT", "U upper IGBT", HeaterDevice.IgbtUUpper, MonitorRelaySelection.None),
        ...
    ];

    public static DeviceRun ForDevice(HeaterDevice device)
    {
        DeviceRun match = All.First(item => item.Device == device);
        return match with { FolderName = match.FolderName[3..] };
    }
}
```

Two things worth calling out:

- **`file`** is an access modifier (a newer one, from C# 11) meaning this
  type is only visible inside `Program.cs` itself — nothing outside this
  file can reference `DeviceRun`. It's a stronger form of `private` that
  applies at the whole-file level, used here because `DeviceRun` and
  `Options` are implementation details of this one file, not part of any
  public API.
- **`with`** — `match with { FolderName = match.FolderName[3..] }` creates a
  *new* `DeviceRun` that's a copy of `match`, except with `FolderName`
  replaced. This is called a **non-destructive mutation**: `match` itself is
  untouched (records default to immutable properties), and you get back a
  new instance. Here it's used to strip the `"NN_"` batch-order prefix (e.g.
  `"01_"`) off the folder name when a device is run standalone rather than
  as part of the full batch — `match.FolderName[3..]` is ordinary C# range
  syntax meaning "everything from index 3 to the end of the string."

### `Options` — a large record used as a settings bag

```csharp
file sealed record Options(
    string Host, int Port, string ArduinoPort, int ArduinoBaudRate, bool AllDevices,
    HeaterDevice? HeaterDevice, double RelaySettleSeconds, ...
    string OutputDirectory, bool ShowHelp)
{
    public static Options Parse(string[] args) { ... }
    ...
}
```

This is the same pattern as `DeviceRun`, just with many more properties: one
immutable, strongly-typed value representing "everything the user configured
on the command line." Once `Options.Parse(args)` builds it, nothing in the
rest of the program can accidentally mutate a setting partway through a run
— there simply isn't a setter to call.

### Why records instead of classes here

You *could* write all of the above as ordinary `class` declarations with
manually written constructors and properties — it would behave almost
identically. Records just remove the boilerplate for the common case this
project needs repeatedly: an immutable value with structural equality,
described positionally. If you ever need a type that's mutable or has
significant internal behaviour and identity (not just data), an ordinary
`class` would be the better choice — but nothing in this file needs that.

---

## Part 3: LINQ

### What LINQ is

LINQ (Language Integrated Query) is a set of extension methods — `Select`,
`Where`, `OrderBy`, and many more — that let you transform and query
collections (arrays, lists, dictionaries, anything implementing
`IEnumerable<T>`) using a declarative, chainable style instead of writing
manual `for`/`foreach` loops. Almost every LINQ method takes a **lambda
expression** — a small inline function like `n => $"CH1_{n}"` — as its
argument, so it's worth reading those as "for each item (called `n` here),
do this."

### Building the channel name arrays

```csharp
string[] equilibriumChannels = Enumerable.Range(1, 12).Select(n => $"CH1_{n}").ToArray();
```

Read this right to left in terms of what happens first:
1. `Enumerable.Range(1, 12)` produces the numbers 1 through 12.
2. `.Select(n => $"CH1_{n}")` transforms each number `n` into the string
   `"CH1_" + n` — this is the same idea as `.map()` in JavaScript/Python if
   you know either of those.
3. `.ToArray()` forces the whole sequence to actually be computed right now,
   and collects it into a real `string[]`.

Without step 3, `Select` wouldn't do any work yet — LINQ queries are **lazy**
by default, meaning the transformation only actually runs when something
(`ToArray()`, `ToList()`, a `foreach`, etc.) asks for the results. This
"materialising" step matters: `ToArray()`/`ToList()` runs the query once and
freezes the result; leaving a query "unmaterialised" means it re-runs from
scratch every time you iterate it again.

The `CH2_8` voltage channel list uses a spread (`..`) alongside the same
pattern:

```csharp
string[] voltageChannels = [.. Enumerable.Range(1, 6).Select(n => $"CH2_{n}"), "CH2_8"];
```
This builds `CH2_1..CH2_6` the same way as above, then the `[.. x, "CH2_8"]`
collection-expression syntax appends `"CH2_8"` as one more element — the
`..` here isn't LINQ itself, it's a C# 12 "spread" that unpacks the LINQ
result into the surrounding array literal.

### Finding the hottest channel: `Select` + `MaxBy`

```csharp
static KeyValuePair<string, double> ResolveTriggerTemperature(
    TemperatureSnapshot snapshot,
    IReadOnlyList<string> equilibriumChannels,
    string configuredChannel) =>
    configuredChannel.Equals("HOTTEST", StringComparison.OrdinalIgnoreCase)
        ? equilibriumChannels
            .Select(channel => new KeyValuePair<string, double>(channel, snapshot.Values[channel]))
            .MaxBy(item => item.Value)
        : new KeyValuePair<string, double>(configuredChannel, snapshot.Values[configuredChannel]);
```

For the `"HOTTEST"` case: `.Select(...)` turns each channel *name* (a
string) into a `(name, value)` pair by looking its current reading up in the
snapshot, and `.MaxBy(item => item.Value)` picks the single pair with the
highest `Value`. `MaxBy` is a good example of LINQ's general shape: you tell
it *what to compare by* (a lambda extracting the value to rank on), and it
handles the comparison and scanning for you.

The exact same pattern appears in `CheckSafetyLimit` to find the single
hottest reading across *all* temperature channels for the overtemperature
cutoff, and in the cooling loop to find the channel with the *worst* (largest)
difference from ambient:

```csharp
KeyValuePair<string, double> worstAmbientDifference = equilibriumChannels
    .Select(channel => new KeyValuePair<string, double>(channel,
        Math.Abs(snapshot.Values[channel] - ambient)))
    .MaxBy(item => item.Value);
```

### Grouping and deduplicating: `Distinct`, `OrderBy`

From `ReadSnapshotAsync`, figuring out which LR8400 "units" (1 or 2) need to
be queried for a given list of channel names:

```csharp
int[] units = channels.Select(channel =>
{
    int separator = channel.IndexOf('_');
    if (!channel.StartsWith("CH", StringComparison.OrdinalIgnoreCase) || separator <= 2 ||
        !int.TryParse(channel.AsSpan(2, separator - 2), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int unitNumber))
        throw new ArgumentException($"Unsupported live analogue channel name: {channel}");
    return unitNumber;
}).Distinct().OrderBy(unit => unit).ToArray();
```

Here `.Select(...)` uses a multi-line lambda (curly braces and an explicit
`return`, instead of the single-expression `=>` form seen earlier) to parse
the unit number out of each channel name — e.g. `"CH2_8"` becomes `2`. Since
many channels share the same unit (`CH1_1`, `CH1_2`, ... all produce `1`),
`.Distinct()` removes duplicates, `.OrderBy(unit => unit)` sorts what's left
into ascending order, and `.ToArray()` materialises it — the result is just
`[1, 2]` for this program's channel set, so the code below only queries each
physical unit once instead of once per channel.

### Filtering: `Where`

Still in `ReadSnapshotAsync`, once the live values for one unit have been
fetched, `Where` picks out just the channels that belong to that unit:

```csharp
foreach (string channel in channels.Where(
             channel => channel.StartsWith($"CH{unit}_", StringComparison.OrdinalIgnoreCase)))
```

`Where` is LINQ's filter — it keeps only the elements for which the lambda
returns `true`. Unlike `Select` (which transforms every element 1-to-1),
`Where` can shrink the sequence.

### Membership checks: `Contains`

In `Options.Parse`, validating that `--trigger-channel` is one of the
allowed CH1 names:

```csharp
if (triggerChannel != "HOTTEST" &&
    !Enumerable.Range(1, 12).Select(n => $"CH1_{n}").Contains(triggerChannel))
    throw new ArgumentException("--trigger-channel must be HOTTEST or CH1_1 through CH1_12.");
```

This builds the same "CH1_1".."CH1_12" list as before, then `.Contains(...)`
asks "is this specific string anywhere in that sequence?" — a simple
existence check.

### Finding one matching item: `First`

From `DeviceRun.ForDevice`, shown earlier:

```csharp
DeviceRun match = All.First(item => item.Device == device);
```

`First` scans the sequence and returns the first element for which the
lambda is `true` — here, the one `DeviceRun` whose `Device` matches the one
requested. (There's also `FirstOrDefault`, which returns a default value
instead of throwing if nothing matches — this code uses `First` deliberately,
because an unmatched `HeaterDevice` here would indicate a bug worth crashing
loudly on, not a case to handle silently.)

### Building a summary string: `Select` + `string.Join`

```csharp
Console.WriteLine($"Run plan: {string.Join(", ", plan.Select(item => item.FolderName))}");
```

`plan.Select(item => item.FolderName)` turns the list of `DeviceRun` records
into a sequence of just their folder names, and `string.Join(", ", ...)`
(not LINQ itself, but commonly paired with it) glues them together into one
comma-separated string for the log line.

### The common shape

Nearly every LINQ example above follows the same three-step shape:

1. Start with a sequence (an array, `Enumerable.Range`, a `List<T>`, ...).
2. Chain one or more transforming/filtering calls (`Select`, `Where`,
   `Distinct`, `OrderBy`, ...), each one taking a small lambda describing
   *what to do per element*.
3. End with something that either materialises the result (`ToArray`,
   `ToList`) or reduces it to a single value (`MaxBy`, `First`, `Contains`,
   `Any`).

Once that shape is familiar, most of the LINQ in this file reads naturally
left-to-right as a small pipeline, even where several calls are chained
together on one line.
