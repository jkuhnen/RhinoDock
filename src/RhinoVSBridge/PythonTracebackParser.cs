using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RhinoVSBridge
{
    internal sealed class PythonTracebackFrame
    {
        public string FilePath { get; }
        public int PythonLineNumber { get; }
        public int VisualStudioLineNumber => PythonTracebackParser.ToVisualStudioLine(PythonLineNumber);
        public string FunctionName { get; }
        public bool IsInternalRunner { get; }
        public bool IsNavigable { get; }

        public PythonTracebackFrame(string filePath, int pythonLineNumber, string functionName,
            bool isInternalRunner, bool isNavigable)
        {
            FilePath = filePath;
            PythonLineNumber = pythonLineNumber;
            FunctionName = functionName;
            IsInternalRunner = isInternalRunner;
            IsNavigable = isNavigable;
        }
    }

    internal sealed class PythonTraceback
    {
        public IReadOnlyList<PythonTracebackFrame> Frames { get; }
        public IReadOnlyList<PythonTracebackFrame> UserFacingFrames { get; }
        public PythonTracebackFrame? PrimaryFrame =>
            UserFacingFrames.Count == 0 ? null : UserFacingFrames[0];
        public string ExceptionMessage { get; }

        public PythonTraceback(IReadOnlyList<PythonTracebackFrame> frames,
            IReadOnlyList<PythonTracebackFrame> userFacingFrames, string exceptionMessage)
        {
            Frames = frames;
            UserFacingFrames = userFacingFrames;
            ExceptionMessage = exceptionMessage;
        }
    }

    internal static class PythonTracebackParser
    {
        private static readonly Regex FramePattern = new Regex(
            "^\\s*File \"(?<file>.*)\", line (?<line>[0-9]+)(?:, in (?<function>.*))?\\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static PythonTraceback Parse(string? traceback)
        {
            if (string.IsNullOrWhiteSpace(traceback))
                return Empty();

            try
            {
                var lines = Regex.Split(traceback!, "\\r\\n|\\n|\\r");
                var frames = new List<PythonTracebackFrame>();
                var lastFrameLine = -1;
                for (var index = 0; index < lines.Length; index++)
                {
                    var match = FramePattern.Match(lines[index]);
                    if (!match.Success ||
                        !int.TryParse(match.Groups["line"].Value, out var pythonLine) ||
                        pythonLine <= 0)
                        continue;

                    var filePath = match.Groups["file"].Value;
                    if (string.IsNullOrWhiteSpace(filePath)) continue;
                    var function = match.Groups["function"].Success
                        ? match.Groups["function"].Value.Trim()
                        : string.Empty;
                    var internalRunner = IsBridgeRunner(filePath);
                    var navigable = !IsPseudoPath(filePath);
                    frames.Add(new PythonTracebackFrame(filePath, pythonLine, function,
                        internalRunner, navigable));
                    lastFrameLine = index;
                }

                var userFrames = frames
                    .Where(frame => frame.IsNavigable && !frame.IsInternalRunner)
                    .Reverse()
                    .ToList();
                return new PythonTraceback(frames, userFrames,
                    ExtractExceptionMessage(lines, lastFrameLine));
            }
            catch
            {
                // A malformed traceback must not affect execution cleanup or the Output pane.
                return Empty();
            }
        }

        public static int ToVisualStudioLine(int pythonLineNumber)
        {
            return pythonLineNumber <= 1 ? 0 : pythonLineNumber - 1;
        }

        private static PythonTraceback Empty()
        {
            return new PythonTraceback(Array.Empty<PythonTracebackFrame>(),
                Array.Empty<PythonTracebackFrame>(), "Python execution failed");
        }

        private static bool IsPseudoPath(string path)
        {
            var trimmed = path.Trim();
            return trimmed.Length == 0 ||
                (trimmed[0] == '<' && trimmed[trimmed.Length - 1] == '>');
        }

        private static bool IsBridgeRunner(string path)
        {
            var normalized = path.Replace('\\', '/');
            var separator = normalized.LastIndexOf('/');
            var fileName = separator >= 0 ? normalized.Substring(separator + 1) : normalized;
            return string.Equals(fileName, "bridge_runner.py", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractExceptionMessage(IReadOnlyList<string> lines, int lastFrameLine)
        {
            for (var index = lines.Count - 1; index > lastFrameLine; index--)
            {
                var raw = lines[index];
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (char.IsWhiteSpace(raw[0])) continue;
                var candidate = raw.Trim().TrimStart('|', '+', '-', ' ');
                if (candidate.Length == 0 ||
                    candidate.StartsWith("Traceback (", StringComparison.Ordinal) ||
                    candidate.StartsWith("During handling of the above exception", StringComparison.Ordinal) ||
                    candidate.StartsWith("The above exception was the direct cause", StringComparison.Ordinal))
                    continue;
                return candidate;
            }
            return "Python execution failed";
        }
    }
}
