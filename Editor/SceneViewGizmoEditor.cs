using UnityEditor;
using UnityEngine;

namespace SceneViewGizmoKit
{
    [CustomPropertyDrawer(typeof(SceneViewGizmoLayerAttribute))]
    public class SceneViewGizmoLayerDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.Integer)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            property.intValue = EditorGUI.LayerField(position, label, property.intValue);
        }
    }

    [CustomEditor(typeof(SceneViewGizmo))]
    public class SceneViewGizmoEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "播放时显示在目标相机画面的角落。点击色锥把相机对齐到该轴，点击中心方块或 Persp/Ortho 切换投影，点击 < 回到上一个视角。相机如果渲染到 RenderTexture，把显示它的 RawImage 拖到 Display。",
                MessageType.Info);
            DrawDefaultInspector();
        }

        [MenuItem("GameObject/Scene View Gizmo", false, 10)]
        static void Create(MenuCommand command)
        {
            GameObject selected = command.context as GameObject;
            if (selected == null)
                selected = Selection.activeGameObject;

            if (selected != null && selected.GetComponent<Camera>() != null && selected.GetComponent<SceneViewGizmo>() == null)
            {
                Undo.AddComponent<SceneViewGizmo>(selected);
                return;
            }

            var go = new GameObject("Scene View Gizmo");
            GameObjectUtility.SetParentAndAlign(go, selected);
            Undo.RegisterCreatedObjectUndo(go, "Create Scene View Gizmo");
            var gizmo = go.AddComponent<SceneViewGizmo>();
            if (Camera.main != null)
                gizmo.TargetCamera = Camera.main;
            Selection.activeGameObject = go;
        }
    }
}
