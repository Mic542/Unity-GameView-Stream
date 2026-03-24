using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace GameViewStream
{
    /// <summary>
    /// Singleton that lets background threads safely schedule actions on Unity's main thread.
    /// Place this component on its own persistent GameObject (created automatically if absent).
    /// </summary>
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>Enqueue an action to run on the next Update tick of the main thread.</summary>
        public static void Enqueue(Action action)
        {
            if (action != null)
                _queue.Enqueue(action);
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoCreate()
        {
            if (_instance != null) return;
            var go = new GameObject("[MainThreadDispatcher]");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MainThreadDispatcher>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            // Drain all pending actions enqueued by background threads
            while (_queue.TryDequeue(out Action action))
            {
                try   { action(); }
                catch (Exception e) { Debug.LogError($"[MainThreadDispatcher] Unhandled exception: {e}"); }
            }
        }
    }
}
