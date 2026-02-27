#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Unity.DemoTeam.Hair
{
    [CustomEditor(typeof(HairRadialPinDriver))]
    public class HairRadialPinDriverEditor : Editor
    {
        private HairRadialPinDriver _t;

        void OnEnable() => _t = (HairRadialPinDriver)target;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space(8);

            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Utilities", EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Add Selected Transforms As Pins"))
                        AddSelectedAsPins();

                    if (GUILayout.Button("Clear Pins"))
                        ClearPins();
                }

                EditorGUILayout.Space(4);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Clear Chain Bindings (GPU)"))
                        _t.ClearChainBindings();

                    if (GUILayout.Button("Rebind Chain Now"))
                        _t.RebindChainNow();
                }

                EditorGUILayout.Space(6);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Bind Hairs To Chains (GPU)"))
                        _t.RequestBindHairsToChainsGPU(true);

                    if (GUILayout.Button("Bind (Keep Existing)"))
                        _t.RequestBindHairsToChainsGPU(false);

                    if (GUILayout.Button("Debug: Dump Chain State"))
                        _t.DumpChainState(4096);
                }
            }
        }

        private void AddSelectedAsPins()
        {
            var sels = Selection.transforms;
            if (sels == null || sels.Length == 0) return;

            Undo.RecordObject(_t, "Add Selected Pins");

            foreach (var tr in sels)
            {
                if (tr == null) continue;

                var pin = new HairRadialPinDriver.Pin
                {
                    transform = tr,
                    radius = 0.15f,
                    pullStrength = 0.15f,
                    pullFalloffPower = 4f,
                    chainStrength = 1f,
                    chainFalloffPower = 6f
                };

                _t.pins.Add(pin);
                if (_t.pins.Count >= HairRadialPinDriver.PIN_MAX)
                    break;
            }

            EditorUtility.SetDirty(_t);
        }

        private void ClearPins()
        {
            Undo.RecordObject(_t, "Clear Pins");
            _t.pins.Clear();
            EditorUtility.SetDirty(_t);
        }

        void OnSceneGUI()
        {
            if (_t == null || _t.pins == null) return;

            int n = Mathf.Min(HairRadialPinDriver.PIN_MAX, _t.pins.Count);
            for (int i = 0; i < n; i++)
            {
                var pin = _t.pins[i];
                if (pin.transform == null) continue;

                Vector3 pos = pin.transform.position;

                EditorGUI.BeginChangeCheck();
                float newR = Handles.RadiusHandle(Quaternion.identity, pos, Mathf.Max(0f, pin.radius));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(_t, "Change Pin Radius");
                    pin.radius = Mathf.Max(0f, newR);
                    _t.pins[i] = pin;
                    EditorUtility.SetDirty(_t);
                }

                if (_t.drawIndexLabels)
                    Handles.Label(pos, $"Pin {i}");
            }
        }
    }
}
#endif