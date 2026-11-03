using System;

namespace RhinoVSBridge
{
    internal static class ErrorListNavigation
    {
        public static int ToTaskProviderLine(int zeroBasedErrorListLine)
        {
            if (zeroBasedErrorListLine < 0)
                throw new ArgumentOutOfRangeException(nameof(zeroBasedErrorListLine));

            return checked(zeroBasedErrorListLine + 1);
        }
    }
}
