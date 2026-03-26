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
            EditorGUILayout.PropertyField(Prop("destroyDisplayOnDisconnect"));

            // ── Performance ──────────────────────────────────────────────────────
            Header("Performance");
            EditorGUILayout.PropertyField(Prop("decodeWorkerCount"));
            EditorGUILayout.PropertyField(Prop("readyQueueCap"));

            // ── Codec ─────────────────────────────────────────────────────────────
            Header("Codec");
            var codecModeProp = Prop("codecMode");
            EditorGUILayout.PropertyField(codecModeProp, new GUIContent("Codec Mode"));

            if (codecModeProp.enumValueIndex == (int)CodecMode.H264)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(Prop("h264StreamWidth"),  new GUIContent("Stream Width"));
                EditorGUILayout.PropertyField(Prop("h264StreamHeight"), new GUIContent("Stream Height"));
                EditorGUI.indentLevel--;
            }

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
