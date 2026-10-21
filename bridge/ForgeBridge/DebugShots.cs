using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace ForgeBridge
{
    /// <summary>Dev aid: FORGEBRIDGE_SHOTS="30,45" saves in-game screenshots (rendered frame, works on a locked desktop).</summary>
    public static class DebugShots
    {
        public static IEnumerator Run()
        {
            string spec = Environment.GetEnvironmentVariable("FORGEBRIDGE_SHOTS");
            if (string.IsNullOrEmpty(spec)) yield break;
            string dir = Environment.GetEnvironmentVariable("FORGEBRIDGE_SHOT_DIR") ?? Application.persistentDataPath;
            Directory.CreateDirectory(dir);
            foreach (var part in spec.Split(','))
            {
                if (!float.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float at)) continue;
                while (Time.realtimeSinceStartup < at) yield return null;
                yield return new WaitForEndOfFrame();
                // whole seconds keep the old names (shot_62.png); fractions get shot_62_5.png
                string name = at == Mathf.Floor(at) ? ((int)at).ToString() : at.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_');
                string path = Path.Combine(dir, "shot_" + name + ".png");
                ScreenCapture.CaptureScreenshot(path);
                Log.Info("screenshot requested: " + path);
            }
        }
    }
}
