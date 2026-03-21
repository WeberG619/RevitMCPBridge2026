using System.Diagnostics;

namespace RevitMCPBridge.Commands
{
    internal static class GenerationState
    {
        public static Process ActiveProcess { get; set; }

        public static bool IsRunning =>
            ActiveProcess != null && !ActiveProcess.HasExited;
    }
}
