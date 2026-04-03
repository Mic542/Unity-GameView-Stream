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

            Header("Server");
            DrawField("server");

            Header("Display");
            DrawField("display");
            DrawField("boundClientId");

            Header("Decode Settings");
            DrawField("maxDecodePerFrame");

            Header("Performance");
            DrawField("decodeWorkerCount");
            DrawField("readyQueueCap");
            EditorGUILayout.HelpBox("Codec is detected automatically from incoming data.", MessageType.Info);

            Header("Debug (read-only)");
            using (new EditorGUI.DisabledScope(true))
            {
                DrawField("_activeCodec", "Active Codec");
                DrawField("_totalFramesDecoded", "Total Frames Decoded");
                DrawField("_totalFramesDropped", "Total Frames Dropped");
            }

            Header("Diagnostics");
            var dumpRecv = serializedObject.FindProperty("debugDumpReceivedH264");
            if (dumpRecv != null)
            {
                EditorGUILayout.PropertyField(dumpRecv, new GUIContent("Dump Received H.264"));
                if (dumpRecv.boolValue)
                {
                    EditorGUI.indentLevel++;
                    DrawField("debugDumpReceivedMaxFrames", "Max Frames (0 = unlimited)");
                    EditorGUI.indentLevel--;
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawField(string name, string label = null)
        {
            var p = serializedObject.FindProperty(name);
            if (p != null)
            {
                if (label != null) EditorGUILayout.PropertyField(p, new GUIContent(label));
                else EditorGUILayout.PropertyField(p);
            }
        }

        private static void Header(string label)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
        }
    }
}
#endif
