// UniTaskChecker.cs — Runs on editor load, detects if UniTask is installed.
// If missing, shows a dialog to install it. Lives in CRVS.Editor asmdef which
// has NO dependency on UniTask or CRVS.Runtime, so it always compiles.

using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace GameViewStream.Editor
{
    [InitializeOnLoad]
    internal static class UniTaskChecker
    {
        private const string UniTaskGitUrl =
            "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask";

        private const string SkipPrefKey = "CRVS_UniTaskInstallSkipped";

        private static ListRequest  s_listReq;
        private static AddRequest   s_addReq;

        static UniTaskChecker()
        {
            // Skip check if user already dismissed it this session
            if (SessionState.GetBool(SkipPrefKey, false)) return;

            s_listReq = Client.List(offlineMode: true);
            EditorApplication.update += OnListProgress;
        }

        private static void OnListProgress()
        {
            if (!s_listReq.IsCompleted) return;
            EditorApplication.update -= OnListProgress;

            if (s_listReq.Status != StatusCode.Success)
            {
                Debug.LogWarning("[CRVS] Could not query Package Manager. UniTask check skipped.");
                return;
            }

            bool hasUniTask = s_listReq.Result.Any(p =>
                p.name == "com.cysharp.unitask");

            if (hasUniTask) return;

            // UniTask is missing — prompt user
            int choice = EditorUtility.DisplayDialogComplex(
                "UniTask Required",
                "UniTask is not installed.\n\n" +
                "CRVS ViewDecoder uses UniTask for non-blocking H.264 hardware decode. " +
                "Without it the project will not compile.\n\n" +
                "Install UniTask now?",
                "Install",      // 0
                "Not Now",      // 1
                "Don't Ask Again This Session"  // 2
            );

            switch (choice)
            {
                case 0:
                    InstallUniTask();
                    break;
                case 2:
                    SessionState.SetBool(SkipPrefKey, true);
                    break;
                // case 1: do nothing
            }
        }

        private static void InstallUniTask()
        {
            Debug.Log("[CRVS] Installing UniTask from Git…");
            s_addReq = Client.Add(UniTaskGitUrl);
            EditorApplication.update += OnAddProgress;
        }

        private static void OnAddProgress()
        {
            if (!s_addReq.IsCompleted) return;
            EditorApplication.update -= OnAddProgress;

            if (s_addReq.Status == StatusCode.Success)
            {
                Debug.Log("[CRVS] UniTask installed successfully. Unity will now recompile.");
            }
            else
            {
                Debug.LogError($"[CRVS] UniTask installation failed: {s_addReq.Error?.message}\n" +
                               $"Install manually via Package Manager → Add package from git URL:\n{UniTaskGitUrl}");
            }
        }
    }
}
