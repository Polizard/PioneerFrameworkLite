using System;

namespace PioneerFramework.Celestial
{
    internal static class CelestialFrameworkStartup
    {
        private static readonly object gate = new();
        private static bool providersInitialized;
        internal static void InitializeProviders()
        {
            lock (gate)
            {
                if (providersInitialized) return;
                var registered = CelestialPhysicsProviderRegistry.Global.Register(ExistingCelestialEphemerisBridge.Id, ExistingCelestialEphemerisBridge.Connect);
                if (!registered.Success) throw new InvalidOperationException("Celestial provider registration failed: " + registered.Errors[0].Code);
                providersInitialized = true;
            }
        }
        internal static void Initialize()
        {
            InitializeProviders();
            CelestialWorldBootstrapHooks.Install();
            CelestialNativeMapLifecycle.Install();
            CelestialSaveGuard.Install();
        }
    }
}
