namespace LoggingSdk
{
#if UNITY

    using System;

    using UnityEngine;

    public class UnityLogger : ILogger
    {
        public void Info(string message) => Debug.Log($"[INFO] {message}");
        public void Warn(string message) => Debug.LogWarning($"[WARN] {message}");
        public void Error(string message) => Debug.LogError($"[ERROR] {message}");
        public void Debug(string message) => Debug.Log($"[DEBUG] {message}");
    }

#endif
}