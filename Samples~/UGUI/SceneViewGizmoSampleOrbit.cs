using UnityEngine;
using UnityEngine.InputSystem;

namespace SceneViewGizmoKit
{
    /// <summary>
    /// 示例场景用的环绕控制。指针在 View 内时，左键、右键或中键拖拽旋转，滚轮缩放。
    /// </summary>
    [DisallowMultipleComponent]
    public class SceneViewGizmoSampleOrbit : MonoBehaviour
    {
        const float LeftDragThreshold = 5f;

        [SerializeField] Transform _pivot;
        [Tooltip("只有指针落在这块区域里才开始旋转或缩放。一般指定显示 RenderTexture 的 RawImage。")]
        [SerializeField] RectTransform _view;
        [SerializeField] float _orbitSpeed = 0.18f;
        [SerializeField] float _zoomSpeed = 1.2f;
        [SerializeField] float _minDistance = 2f;
        [SerializeField] float _maxDistance = 24f;

        float _distance = 8f;
        float _yaw = 35f;
        float _pitch = 28f;
        Vector2 _leftPressPosition;
        bool _leftDragging;
        bool _leftPressOnView;
        bool _leftPressOnGizmo;
        bool _rightFromView;
        bool _middleFromView;
        SceneViewGizmo _gizmo;

        public Transform Pivot
        {
            get { return _pivot; }
            set { _pivot = value; }
        }

        void LateUpdate()
        {
            if (_pivot == null)
                return;

            Mouse mouse = Mouse.current;
            if (mouse == null)
                return;

            Vector2 screen = mouse.position.ReadValue();
            if (mouse.rightButton.wasPressedThisFrame)
                _rightFromView = ContainsView(screen);
            if (!mouse.rightButton.isPressed)
                _rightFromView = false;
            if (mouse.middleButton.wasPressedThisFrame)
                _middleFromView = ContainsView(screen);
            if (!mouse.middleButton.isPressed)
                _middleFromView = false;

            bool orbiting = (mouse.rightButton.isPressed && _rightFromView)
                || (mouse.middleButton.isPressed && _middleFromView)
                || LeftDragOrbit(mouse, screen);

            float scroll = mouse.scroll.ReadValue().y;
            bool zooming = Mathf.Abs(scroll) > 0.01f && ContainsView(screen);
            if (!orbiting)
                ReadPose();

            if (orbiting)
            {
                Vector2 delta = mouse.delta.ReadValue();
                _yaw += delta.x * _orbitSpeed;
                _pitch = Mathf.Clamp(_pitch - delta.y * _orbitSpeed, -85f, 85f);
            }

            if (zooming)
                _distance = Mathf.Clamp(_distance - scroll * 0.012f * _zoomSpeed, _minDistance, _maxDistance);

            if (orbiting || zooming)
                WritePose();
        }

        bool LeftDragOrbit(Mouse mouse, Vector2 screen)
        {
            if (mouse.leftButton.wasPressedThisFrame)
            {
                _leftPressPosition = screen;
                _leftDragging = false;
                _leftPressOnView = ContainsView(screen);
                if (_gizmo == null)
                    _gizmo = GetComponent<SceneViewGizmo>();
                _leftPressOnGizmo = _leftPressOnView && _gizmo != null && _gizmo.ContainsScreenPoint(screen);
            }

            if (!mouse.leftButton.isPressed || !_leftPressOnView || _leftPressOnGizmo)
            {
                _leftDragging = false;
                return false;
            }

            if (!_leftDragging)
            {
                Vector2 moved = mouse.position.ReadValue() - _leftPressPosition;
                _leftDragging = moved.sqrMagnitude >= LeftDragThreshold * LeftDragThreshold;
            }

            return _leftDragging;
        }

        bool ContainsView(Vector2 screen)
        {
            if (_view == null || !_view.gameObject.activeInHierarchy)
                return false;

            return RectTransformUtility.RectangleContainsScreenPoint(_view, screen, EventCamera(_view));
        }

        static Camera EventCamera(RectTransform view)
        {
            Canvas canvas = view.GetComponentInParent<Canvas>();
            if (canvas == null)
                return null;

            Canvas root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            if (root.renderMode == RenderMode.ScreenSpaceOverlay)
                return null;

            return root.worldCamera != null ? root.worldCamera : Camera.main;
        }

        void ReadPose()
        {
            Vector3 offset = transform.position - _pivot.position;
            if (offset.sqrMagnitude < 0.0001f)
                return;

            _distance = offset.magnitude;
            Vector3 look = -offset / _distance;
            _pitch = Mathf.Asin(Mathf.Clamp(-look.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (Mathf.Abs(look.y) < 0.98f)
                _yaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
        }

        void WritePose()
        {
            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 position = _pivot.position - rotation * Vector3.forward * _distance;
            transform.SetPositionAndRotation(position, rotation);
        }
    }
}
