using UnityEngine;
using Wizards.Mtga;

namespace ForgeBridge
{
    /// <summary>
    /// Runtime half of "no Wizards, no tracking" (the Patcher does the rest: bootstrap config without BI tracker,
    /// Backtrace, Steam and Unity Services; no manifest pointer request). Unity's own analytics/crash reporting are
    /// engine settings, switched off here as early as managed code runs; the firewall rule still covers the engine's
    /// boot-time ping. The environment loses every Wizards endpoint, so nothing can reach them by accident: BI events
    /// without a bike URI go through the (local) Front Door.
    /// </summary>
    public static class Privacy
    {
        private const string Nowhere = "http://127.0.0.1:9/";

        public static void Apply()
        {
            UnityEngine.Analytics.Analytics.initializeOnStartup = false;
            UnityEngine.Analytics.Analytics.enabled = false;
            UnityEngine.Analytics.Analytics.deviceStatsEnabled = false;
            UnityEngine.Analytics.Analytics.limitUserTracking = true;
            UnityEngine.Analytics.PerformanceReporting.enabled = false;
            UnityEngine.CrashReportHandler.CrashReportHandler.enableCaptureExceptions = false;

            var env = Pantry.CurrentEnvironment;
            if (env == null)
            {
                Log.Error("Privacy: no current environment to scrub");
                return;
            }
            env.bikeUri = "";
            env.doorbellUri = Nowhere;
            env.ecoUri = Nowhere;
            env.accountSystemBaseUri = Nowhere;
            env.digitProfileBaseUri = Nowhere;
            env.sgsUri = Nowhere;
            env.accountSystemId = env.accountSystemSecret = "";
            env.epicWASClientId = env.epicWASClientSecret = "";
            env.steamClientId = env.steamClientSecret = "";
            env.digitProfileApiKey = "";
            Log.Info("Privacy: Unity analytics off, Wizards endpoints cleared from environment '" + env.name + "'");
        }
    }
}
