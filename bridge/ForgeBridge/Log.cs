using UnityEngine;

namespace ForgeBridge
{
    public static class Log
    {
        public static void Info(string msg) { Debug.Log("[ForgeBridge] " + msg); }
        public static void Error(string msg) { Debug.LogError("[ForgeBridge] " + msg); }
    }
}
