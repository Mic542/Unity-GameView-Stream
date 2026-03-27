#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace GameViewStream
{
    [CustomEditor(typeof(ViewDecoder))]
    public sealed class ViewDecoderEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // ── Server ───────────────────────────────────────────────────────────
            Header("Server");
            EditorGUILayout.PropertyField(Prop("server"));

            // ── Display ──────────────────────────────────────────────────────────
            Header("Display");
            EditorGUILayout.PropertyField(Prop("display"));
            EditorGUILayout.PropertyField(Prop("boundClientId"));

            // ── Decode Settings ──────────────────────────────────────────────────
            Header("Decode Settings");
            EditorGUILayout.PropertyField(Prop("maxDecodePerFrame"));

            // ── Performance ──────────────────────────────────────────────────────
            Header("Performance");
            EditorGUILayout.PropertyField(Prop("decodeWorkerCount"));
            EditorGUILayout.PropertyField(Prop("readyQueueCap"));
            EditorGUILayout.HelpBox(
                "Codec is detected automatically from the incoming stream.\n"
              + "\u2022 MJPEG: multiple workers scale with client count.\n"
              + "\u2022 H.264: workers share a per-client lock for sequential NAL feeding.",
                MessageType.Info);

            // ── Debug ─────────────────────────────────────────────────────────────
            Header("Debug (read-only)");
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(Prop("_totalFramesDecoded"), new GUIContent("Total Frames Decoded"));

            serializedObject.ApplyModifiedProperties();
        }

        private SerializedProperty Prop(string name) => serializedObject.FindProperty(name);

        private static void Header(string label)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
        }
    }
}
#endif
