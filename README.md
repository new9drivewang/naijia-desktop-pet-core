# Naijia Desktop Pet Core — geometric engine demo

A small Windows desktop-pet engine demonstration derived from the v7.6
source snapshot. The visible shapes are original, code-generated engineering
placeholders, not the original characters. This is a no-charge, noncommercial source-available demo under the retained
PolyForm Noncommercial License 1.0.0; see LICENSE and NOTICE.md. It is not
MIT-licensed or described as OSI open source. Parent publication review is pending.

It demonstrates transparent layered windows, asynchronous frame decoding,
a bounded shared frame cache, dragging/throwing, body-zone input, procedural
belly deformation, recovery fades, multiple pets and session-only preferences.
The 96-frame geometric motion lasts four seconds; the second tile is static.
Menus inherited from the engine are mainly Chinese.

Windows 10/11, .NET Framework 4.x and Windows PowerShell 5.1 are the intended
runtime. Linux and macOS are not supported desktop runtimes. No administrator
rights, package installation, paid service or network connection is needed
to launch when those Windows components are already available.

1. Extract the entire source ZIP to a folder.
2. Double-click `Launch.cmd`. It compiles the local C# files in memory via
   Windows PowerShell and starts one geometric placeholder. Startup can take
   a few seconds. There is no prebuilt executable.
3. Right-click a shape or the tray icon for actions, size, pause, role switching
   and exit. `Stop.cmd` signals only this demo's instance to close.

On the blue placeholder, click the yellow circle for motion or the pale ellipse
for a procedural bounce. Drag to move it; a double-click on other body areas
can split a pet (maximum six). Optional hover interaction starts disabled.
These controls still require native Windows and physical-input validation.

The demo contains no weather panel, telemetry, network client, update checker,
automatic download, geolocation or autostart. It does not read or write the
private app's settings. Options reset on exit; no location or preferences are
saved. Launchers do not alter execution policy or permissions. PowerShell's
compiler may use OS temporary files. The retained opt-in `RunProfile` API can
write a local performance report if explicitly invoked by a developer; normal
launch does not invoke it. Audio code remains but no audio is supplied and
sound defaults off. Fullscreen avoidance is optional and defaults off.

`Check.cmd` checks the packaged file hashes and decodes PNGs on Windows.
`Test-Windows.cmd` runs the inherited bounded cache/GDI regression checks;
it does not validate desktop input or smoothness. Cross-platform logic tests:
`dotnet run --project tests/CoreTests.csproj` and
`dotnet run --project tests/cache/CacheTests.csproj` (requires .NET 8 SDK).
`python tools/test_export.py` validates manifest integrity, exact placeholder
reproduction and PNG data with Python 3. Regenerate artwork, when deliberately
changing it, with `python tools/generate_placeholders.py`; then rebuild the
file manifest with `python tools/make_manifest.py`.

The export uses only `candidate-v7.6` at commit
`19f74776a0d86d0415733497b081a3de94e7847f` and relevant tests from that same
commit. This commit underlies the private v7.6-preview2 prerelease; it is not a
claim of a fully validated stable product. Original character media, releases,
build outputs, user configuration and git history are excluded. The source
provenance, attribution and retained license conditions are in NOTICE.md.

Validation of this exact export: see VALIDATION.md. Linux compilation and
logic/data checks do not establish native Windows rendering, PNG/GDI lifetime,
launcher execution, physical mouse feel, audio, multi-DPI/multi-monitor behavior,
long-term reliability, screen FPS or visual acceptance. Original characters
were neither included nor validated. A keyword scan is not a security audit.
