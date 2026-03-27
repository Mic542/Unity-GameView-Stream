#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace GameViewStream
{
    [CustomEditor(typeof(StreamServer))]
    public sealed class StreamServerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // ── Server Settings ──────────────────────────────────────────────────
            Header("Server Settings");
            EditorGUILayout.PropertyField(Prop("transportMode"), new GUIContent("Transport Mode"));
            EditorGUILayout.PropertyField(Prop("port"),       new GUIContent("Port"));
            EditorGUILayout.PropertyField(Prop("maxClients"), new GUIContent("Max Clients"));

            // ── Auto-Discovery ───────────────────────────────────────────────────
            Header("Auto-Discovery");
            var enableDiscoveryProp = Prop("enableDiscovery");
            EditorGUILayout.PropertyField(enableDiscoveryProp, new GUIContent("Enable Discovery"));

            if (enableDiscoveryProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(Prop("discoveryPort"), new GUIContent("Discovery Port"));
                EditorGUI.indentLevel--;
            }

            // ── Connection ───────────────────────────────────────────────────────
            Header("Connection");
            EditorGUILayout.PropertyField(Prop("heartbeatInterval"), new GUIContent("Heartbeat Interval (s)"));
            EditorGUILayout.PropertyField(Prop("heartbeatTimeout"),  new GUIContent("Heartbeat Timeout (s)"));
            EditorGUILayout.PropertyField(Prop("frameQueueCap"),     new GUIContent("Frame Queue Cap"));

            // ── Debug ─────────────────────────────────────────────────────────────
            Header("Debug (read-only)");
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(Prop("_connectedClients"), new GUIContent("Connected Clients"));

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
