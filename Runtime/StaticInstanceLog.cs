using System.Diagnostics;

namespace StaticInstances
{
    public static class StaticInstanceLog
    {
        /// <summary>
        /// Informational logging that vanishes from release builds, argument
        /// construction included. Editor and development builds are unchanged.
        /// DEBUG is Unity's UNITY_EDITOR || DEVELOPMENT_BUILD; 6000.6 deprecated
        /// DEVELOPMENT_BUILD as a directive (UAC0009).
        /// </summary>
        [Conditional("DEBUG")]
        public static void Info(string message)
        {
            UnityEngine.Debug.Log(message);
        }

        /// <inheritdoc cref="Info(string)"/>
        [Conditional("DEBUG")]
        public static void Info(string message, UnityEngine.Object context)
        {
            UnityEngine.Debug.Log(message, context);
        }
    }
}
