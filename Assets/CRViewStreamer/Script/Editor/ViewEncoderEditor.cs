#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace GameViewStream
{
    [CustomEditor(typeof(ViewEncoder))]
    public sealed class ViewEncoderEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // ── Server Connection ────────────────────────────────────────────────
            Header("Server Connection");
            var autoDiscoverProp = Prop("autoDiscover");
            EditorGUILayout.PropertyField(autoDiscoverProp, new GUIContent("Auto Discover"));

            EditorGUI.indentLevel++;
            if (autoDiscoverProp.boolValue)
            {
                EditorGUILayout.PropertyField(Prop("discoveryPort"),    new GUIContent("Discovery Port"));
                EditorGUILayout.PropertyField(Prop("discoveryTimeout"), new GUIContent("Timeout (ms)"));
                EditorGUILayout.PropertyField(Prop("serverPort"),       new GUIContent("Server Port"));
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.PropertyField(Prop("serverAddress"), new GUIContent("Fallback Address"));
            }
            else
            {
                EditorGUILayout.PropertyField(Prop("serverAddress"), new GUIContent("Server Address"));
                EditorGUILayout.PropertyField(Prop("serverPort"),    new GUIContent("Server Port"));
            }
            EditorGUI.indentLevel--;

            // ── Capture Settings ─────────────────────────────────────────────────
            Header("Capture Settings");
            EditorGUILayout.PropertyField(Prop("captureWidth"),  new GUIContent("Width"));
            EditorGUILayout.PropertyField(Prop("captureHeight"), new GUIContent("Height"));
            EditorGUILayout.PropertyField(Prop("targetFPS"),     new GUIContent("Target FPS"));

            // ── Codec ─────────────────────────────────────────────────────────────
            Header("Codec");
            var codecModeProp = Prop("codecMode");
            EditorGUILayout.PropertyField(codecModeProp, new GUIContent("Codec Mode"));

            EditorGUI.indentLevel++;
            if (codecModeProp.enumValueIndex == (int)CodecMode.MJPEG)
            {
                EditorGUILayout.PropertyField(Prop("jpegQuality"),     new GUIContent("Quality"));
                EditorGUILayout.PropertyField(Prop("jpegSubsampling"), new GUIContent("Chroma Subsampling"));
            }
            else // H264
            {
                EditorGUILayout.PropertyField(Prop("h264BitrateMbps"), new GUIContent("Bitrate (Mbps)"));
                EditorGUILayout.HelpBox("H.264 encode requires Android device with MediaCodec.\nNo effect in Windows Editor — JPEG is used at runtime.", MessageType.Info);
            }
            EditorGUI.indentLevel--;

            // ── Queue Settings ────────────────────────────────────────────────────
            Header("Queue Settings");
            EditorGUILayout.PropertyField(Prop("rawQueueCapacity"),  new GUIContent("Raw Queue Capacity"));
            EditorGUILayout.PropertyField(Prop("sendQueueCapacity"), new GUIContent("Send Queue Capacity"));

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
