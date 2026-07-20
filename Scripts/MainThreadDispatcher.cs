using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Static utility that marshals actions from background threads back to Unity's main thread.
    /// Uses a ConcurrentQueue drained each frame via Update().
    /// 
    /// Usage:
    ///   // From any background thread:
    ///   MainThreadDispatcher.Enqueue(() => { /* Unity API calls here */ });
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static int _mainThreadId = -1;
        private static readonly ConcurrentQueue<Action> s_queue = new ConcurrentQueue<Action>();

        /// <summary>
        /// Get or create the singleton dispatcher. Must be called from the main thread only.
        /// Call once at mod startup via Touch() to ensure GameObject creation stays on-thread.
        /// </summary>
        public static MainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("MainThreadDispatcher");
                    _instance = go.AddComponent<MainThreadDispatcher>();
                    DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    Log.Debug(() => "MainThreadDispatcher created");
                }
                return _instance;
            }
        }

        /// <summary>
        /// Enqueue an action to be executed on Unity's main thread during the next Update().
        /// Safe to call from any thread. If already on the main thread, executes immediately.
        /// Uses a static queue — never touches Instance or creates GameObjects off-thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;

            // Already on main thread: execute immediately
            if (_mainThreadId != -1 && System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                action();
                return;
            }

            // Background thread: queue to static ConcurrentQueue (pure .NET, no Unity API).
            s_queue.Enqueue(action);
        }

        /// <summary>
        /// Touch the dispatcher and capture main thread ID.
        /// Call from mod startup on the main thread for fastest detection
        /// (before any background task can call Enqueue).
        /// </summary>
        public static void Touch()
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            _ = Instance;  // create GameObject on main thread
        }

        void Update()
        {
            if (_mainThreadId == -1)
                _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            while (s_queue.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error($"MainThreadDispatcher: Exception executing enqueued action: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// Get the number of pending actions in the queue (for diagnostics).
        /// </summary>
        public int PendingCount => s_queue.Count;

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
