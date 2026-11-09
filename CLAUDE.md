# AgentDock

## Rules

- Never kill or restart the running `AgentDock.exe` process. The user runs it live from Visual Studio for other active coding sessions at the same time; killing it interrupts unrelated, unsaved work. If a build fails because the exe/DLL is locked by the running process, just report that the code change is made and compiles, note that the exe is locked, and stop there. Let the user rebuild/relaunch themselves when ready.
