# BetterGI

`RdpActiveXHost.cs`, `ChildSessionNativeMethods.cs` and
`ChildSessionProcessLauncher.cs` are adapted from BetterGI:

https://github.com/babalae/better-genshin-impact/tree/6a8a9a62232855069f0244bc992f66772f14fe0b

Copyright: BetterGI contributors. License: GNU GPL version 3 (see the project
root LICENSE, also distributed alongside this helper).

Original paths:

- `BetterGenshinImpact/View/Controls/ChildSession/RdpActiveXHost.cs`
- `BetterGenshinImpact/Service/ChildSession/ChildSessionNativeMethods.cs`
- `BetterGenshinImpact/Service/ChildSession/ChildSessionProcessLauncher.cs`

Changes: namespaces and application names, removal of BetterGI-specific launch
arguments, and exposure of the elevated launcher for the Python entry point.
The RDP connection, extended settings, COM event sink, keyboard forwarding and
temporary scheduled-task launch retain BetterGI's implementation.

The session manager, bounded mouse transport and Python integration are part of
March7thAssistant and are covered by its GPL-3.0 license.
