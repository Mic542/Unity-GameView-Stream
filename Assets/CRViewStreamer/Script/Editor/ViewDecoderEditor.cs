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

            // Read codecMode early so we can use it in Performance and Codec sections
            var codecModeProp = Prop("codecMode");

            // ── Performance ──────────────────────────────────────────────────────
            Header("Performance");
            bool isH264 = codecModeProp.enumValueIndex == (int)CodecMode.H264;
            using (new EditorGUI.DisabledScope(isH264))
            {
                EditorGUILayout.PropertyField(Prop("decodeWorkerCount"));
                if (isH264)
                    EditorGUILayout.HelpBox("H.264 requires strictly sequential NAL feeding — worker count is forced to 1 at runtime.", MessageType.Info);
            }
            EditorGUILayout.PropertyField(Prop("readyQueueCap"));

            // ── Codec ───────────────────────────────────────────────────────────
            Header("Codec");
            EditorGUILayout.PropertyField(codecModeProp, new GUIContent("Codec Mode"));
            // if (isH264)
            //     EditorGUILayout.HelpBox("Stream resolution is detected automatically from the H.264 SPS header — no manual width/height needed.", MessageType.Info);

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
