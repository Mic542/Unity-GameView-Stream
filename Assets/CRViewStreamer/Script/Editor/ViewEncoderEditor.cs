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
                EditorGUILayout.PropertyField(Prop("serverAddress"),        new GUIContent("Server Address"));
                EditorGUILayout.PropertyField(Prop("serverPort"),           new GUIContent("Server Port"));
                EditorGUILayout.PropertyField(Prop("manualTransportMode"),  new GUIContent("Transport Mode"));
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

            // ── Reliable UDP ──────────────────────────────────────────────────────
            Header("Reliable UDP");
            EditorGUILayout.PropertyField(Prop("sendReliable"), new GUIContent("Send Reliable"));

            var reliableProp = Prop("sendReliable");
            if (reliableProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(Prop("retransmitMs"), new GUIContent("Retransmit (ms)"));
                EditorGUILayout.PropertyField(Prop("maxRetries"),   new GUIContent("Max Retries"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.HelpBox(
                "Only applies when the server selects UDP transport mode.\n"
              + "Adds sequence numbers and ACK-based retransmission to prevent packet loss on WiFi.",
                MessageType.Info);

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
