# Validation of this export

Executed on 2026-10-26 in Debian GNU/Linux 13, x86-64; .NET SDK 8.0.427,
Roslyn compiler from that SDK, Microsoft.NETFramework.ReferenceAssemblies.net48
1.0.3, .NET runtime 8.0.33 and Python 3.12. Windows is unavailable here.

| Check | Result | Scope |
| --- | --- | --- |
| All 11 production C# files, C# 5, net48 references | PASS; zero warnings/errors | Real compilation as library, not native execution |
| CoreTests | PASS; 18 assertions | Real interaction/hover/effect/default-setting code; no UI |
| Cache tests | PASS; 13 scenarios | Real FrameCache.cs; explicit Bitmap and I/O doubles |
| Export data tests | PASS; 7 tests | Relative manifest hashes/sizes, exact generated asset bytes, complete PNG chunk/CRC/RGBA streams, exclusions and narrow offline-code checks |
| Native Windows startup / Add-Type / CMD launchers | NOT RUN | Requires Windows PowerShell 5.1 and .NET Framework |
| Test-Windows.cmd inherited cache/GDI checks | NOT RUN | Compiled with production code, not executed |
| WinForms transparency / UpdateLayeredWindow / GDI handles | NOT RUN | Linux logic/data checks cannot establish these |
| Real mouse feel, drag/throw/split, tray, pause/resume | NOT RUN | No physical input acceptance |
| Multi-DPI, multi-monitor, fullscreen, FPS, long sessions | NOT RUN | No desktop or performance acceptance |
| Original character visuals/audio | EXCLUDED | No original assets supplied; no rights or validation implied |

Prior private-release Windows results are not inherited by this modified export.
The engine's function, interaction and visual acceptance remain separate.
Placeholder and pixel/data checks do not validate the original character or a
desktop presentation. No claim of security follows from keyword scanning.

The cloud build used Roslyn directly with the six net48 reference assemblies
`mscorlib`, `System`, `System.Core`, `System.Windows.Forms`, `System.Drawing`
and `System.Xml`. It produced a temporary local library outside the export;
no executable, DLL, PDB, compiler package or build directory is distributed.
All 116 PNGs also decoded successfully through Pillow 12.3.0 on Linux. This
does not substitute for Windows GDI decoding. The inherited Windows regression
fixture indices were adjusted to the demo's 96 action paths while retaining
84-frame cache pressure and the full 64-slot queue assertions.

Core/cache tests were compiled against local .NET 8 references and executed
with the local runtime. There were no Actions runs or external source uploads.

To finish native validation, launch all four CMD files on Windows and record
the exact export hash and environment. Verify real input, transparent edges,
action completion, pause/recovery, optional hover, six-pet exit, scaling and
multiple monitors. Keep functional checks separate from physical feel and
visual review. This is a review checklist, not an automatic workflow.
