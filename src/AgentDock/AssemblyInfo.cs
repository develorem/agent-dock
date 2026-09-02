using System.Runtime.CompilerServices;
using System.Windows;

// The chat turn state machine is driven by ClaudeSession events, which only that class can
// raise — so the tests call ChatTurnProcessor's handlers directly instead. Its classification
// rules are the most regression-prone code in the app and were previously untestable.
[assembly: InternalsVisibleTo("AgentDock.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
