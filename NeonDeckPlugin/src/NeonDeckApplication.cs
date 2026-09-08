namespace Loupedeck.NeonDeckPlugin
{
    using System;

    // Neon Deck is a universal (no-application) plugin, but the host still expects a ClientApplication type to exist.
    public class NeonDeckApplication : ClientApplication
    {
        public NeonDeckApplication()
        {
        }

        protected override String GetProcessName() => "";

        protected override String GetBundleName() => "";

        public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
    }
}
