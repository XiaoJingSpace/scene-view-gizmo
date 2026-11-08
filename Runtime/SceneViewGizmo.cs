using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SceneViewGizmoKit
{
    public enum SceneViewGizmoCorner
    {
        TopLeft,
        TopRight,
        BottomRight,
        BottomLeft
    }

    public enum SceneViewGizmoAlignMode
    {
        Orbit,
        InPlace
    }

    /// <summary>
    /// 画面角落的视角坐标轴。点击轴对齐相机，点击中心或 Persp/Ortho 切换投影。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    [AddComponentMenu("Scene View Gizmo/Scene View Gizmo")]
    public class SceneViewGizmo : MonoBehaviour
    {
        const int CenterPart = 6;
        const float VisibleAlpha = 0.12f;

        static readonly Vector3[] Axes =
        {
            Vector3.right,
            Vector3.up,
            Vector3.forward,
            Vector3.left,
            Vector3.down,
            Vector3.back
        };

        static readonly string[] AxisLabels = { "X", "Y", "Z" };

        [Header("相机")]
        [Tooltip("要跟随并控制的相机。留空时使用同一物体上的 Camera，否则使用主相机。")]
        [SerializeField] Camera _targetCamera;
        [Tooltip("绕转中心。留空时，对齐会绕相机前方的一点转动。")]
        [SerializeField] Transform _pivot;
        [Tooltip("勾选后，点击坐标轴会直接转动目标相机。")]
        [SerializeField] bool _controlCamera = true;
        [SerializeField] SceneViewGizmoAlignMode _alignMode = SceneViewGizmoAlignMode.Orbit;
        [Tooltip("没有指定枢轴时，相机与观察点的距离。")]
        [SerializeField, Min(0.01f)] float _orbitDistance = 5f;
        [Tooltip("对齐动画时长，单位秒。0 表示立刻对齐。")]
        [SerializeField, Min(0f)] float _alignDuration = 0.35f;

        [Header("位置")]
        [Tooltip("相机渲染到 RenderTexture 时，指定显示这块纹理的界面，一般是 RawImage。坐标轴画进纹理，文字和点击跟这块区域对齐。留空则直接画在屏幕上。")]
        [SerializeField] RectTransform _display;
        [SerializeField] SceneViewGizmoCorner _corner = SceneViewGizmoCorner.TopRight;
        [SerializeField, Min(48f)] float _screenSize = 108f;
        [Tooltip("相对所选角落的屏幕偏移，单位像素。Y 向上为正。")]
        [SerializeField] Vector2 _screenOffset;

        [Header("外观")]
        [SerializeField] Color _positiveX = new Color(219f / 255f, 62f / 255f, 29f / 255f, 1f);
        [SerializeField] Color _positiveY = new Color(154f / 255f, 243f / 255f, 72f / 255f, 1f);
        [SerializeField] Color _positiveZ = new Color(58f / 255f, 122f / 255f, 248f / 255f, 1f);
        [SerializeField] Color _negativeAxis = new Color(204f / 255f, 204f / 255f, 204f / 255f, 1f);
        [SerializeField] Color _centerColor = new Color(235f / 255f, 235f / 255f, 235f / 255f, 1f);
        [SerializeField] Color _hoveredColor = new Color(246f / 255f, 242f / 255f, 50f / 255f, 1f);
        [SerializeField] bool _showProjectionLabel = true;

        struct GizmoDraw
        {
            public Mesh Mesh;
            public Matrix4x4 Matrix;
            public Color Color;
            public float Depth;
        }

        struct CameraPose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Orthographic;
            public float OrthographicSize;
            public float FieldOfView;
        }

        readonly List<GizmoDraw> _draws = new List<GizmoDraw>(8);
        readonly float[] _axisAlpha = new float[6];
        readonly float[] _axisAlphaTarget = new float[6];

        GameObject _rig;
        Camera _gizmoCamera;
        Material _material;
        Camera _boundCamera;
        bool _warnedMissingCamera;
        bool _warnedMissingMaterial;
        bool _warnedDisplayTexture;
        int _hoveredPart = -1;
        bool _hasPointer;

        bool _aligning;
        float _alignT;
        Camera _alignCamera;
        Quaternion _alignFromRotation;
        Quaternion _alignToRotation;
        Vector3 _alignFromPosition;
        Vector3 _alignToPosition;

        bool _hasPrevious;
        CameraPose _previousPose;

        GUIStyle _axisStyle;
        GUIStyle _projectionStyle;

        bool _displayActive;
        float _viewPerScreen = 1f;
        float _textureWidth = 1f;
        float _textureHeight = 1f;
        float _displayScreenWidth = 1f;
        Rect _uvRect = new Rect(0f, 0f, 1f, 1f);
        Rect _gizmoViewRect;
        Camera _eventCamera;
        Vector2 _pointerView;
        RectTransform _labelRoot;
        Text _labelX;
        Text _labelY;
        Text _labelZ;
        Text _labelBack;
        Text _labelMode;
        PointerEventData _uiPointer;
        readonly List<RaycastResult> _uiHits = new List<RaycastResult>(8);

        public Camera TargetCamera
        {
            get { return _targetCamera; }
            set { _targetCamera = value; }
        }

        public virtual Transform Pivot
        {
            get { return _pivot; }
            set { _pivot = value; }
        }

        public bool ControlCamera
        {
            get { return _controlCamera; }
            set { _controlCamera = value; }
        }

        public virtual RectTransform Display
        {
            get { return _display; }
            set { _display = value; }
        }

        public event Action<Vector3> AxisClicked;
        public event Action ProjectionClicked;

        public void AlignTo(Vector3 worldAxis)
        {
            if (worldAxis.sqrMagnitude < 1e-8f)
                return;

            Vector3 axis = worldAxis.normalized;
            if (AxisClicked != null)
                AxisClicked(axis);
            if (!_controlCamera)
                return;

            Camera camera = ResolveCamera();
            if (camera == null)
                return;

            if (!_aligning)
                Remember(camera);

            Quaternion targetRotation = RotationLookingAlong(camera.transform, -axis);
            Vector3 targetPosition = camera.transform.position;
            if (_alignMode == SceneViewGizmoAlignMode.Orbit)
            {
                Vector3 pivot = PivotPoint(camera);
                float distance = Vector3.Distance(camera.transform.position, pivot);
                if (distance < 0.01f)
                    distance = Mathf.Max(0.01f, _orbitDistance);
                targetPosition = pivot - targetRotation * Vector3.forward * distance;
            }

            _alignCamera = camera;
            _alignFromRotation = camera.transform.rotation;
            _alignToRotation = targetRotation;
            _alignFromPosition = camera.transform.position;
            _alignToPosition = targetPosition;
            _alignT = 0f;
            _aligning = _alignDuration > 0f;
            if (!_aligning)
                ApplyAlign(1f);
        }

        public void ToggleProjection()
        {
            if (ProjectionClicked != null)
                ProjectionClicked();
            if (!_controlCamera)
                return;

            Camera camera = ResolveCamera();
            if (camera == null)
                return;

            _aligning = false;
            Remember(camera);
            if (camera.orthographic)
            {
                camera.orthographic = false;
                return;
            }

            float distance = Pivot != null
                ? Vector3.Distance(camera.transform.position, Pivot.position)
                : Mathf.Max(0.01f, _orbitDistance);
            camera.orthographicSize = Mathf.Max(0.01f, distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
            camera.orthographic = true;
        }

        public void RestorePreviousView()
        {
            if (!_hasPrevious || !_controlCamera)
                return;

            Camera camera = ResolveCamera();
            if (camera == null)
                return;

            _aligning = false;
            CameraPose current = Capture(camera);
            ApplyPose(camera, _previousPose);
            _previousPose = current;
        }

        /// <summary>
        /// 屏幕点是否落在坐标轴或底部投影标签上。坐标原点在左下角。
        /// </summary>
        public bool ContainsScreenPoint(Vector2 screenPosition)
        {
            if (_gizmoCamera == null || !_gizmoCamera.enabled || _gizmoViewRect.width < 1f)
                return false;
            if (!TryScreenToView(screenPosition, out Vector2 view))
                return false;

            Rect pixel = _gizmoViewRect;
            float pad = 20f * _viewPerScreen;
            float label = LabelHeight() * _viewPerScreen;
            pixel.x -= pad;
            pixel.y -= pad + label;
            pixel.width += pad * 2f;
            pixel.height += pad * 2f + label;
            return pixel.Contains(view);
        }

        void Awake()
        {
            for (int i = 0; i < _axisAlpha.Length; i++)
            {
                _axisAlpha[i] = 1f;
                _axisAlphaTarget[i] = 1f;
            }
        }

        void OnEnable()
        {
            if (!Application.isPlaying)
                return;

            SceneViewGizmoGeometry.Warmup();
            _material = CreateMaterial();
        }

        void OnDisable()
        {
            if (_gizmoCamera != null)
                _gizmoCamera.enabled = false;
        }

        void OnDestroy()
        {
            if (_material != null)
                Destroy(_material);
            if (_rig != null)
                Destroy(_rig);
            if (_labelRoot != null)
                Destroy(_labelRoot.gameObject);
        }

        void LateUpdate()
        {
            if (!Application.isPlaying)
                return;

            Camera target = ResolveCamera();
            if (target == null)
            {
                if (_gizmoCamera != null)
                    _gizmoCamera.enabled = false;
                HideLabels();
                if (!_warnedMissingCamera)
                {
                    Debug.LogWarning("Scene View Gizmo 没有找到目标相机。", this);
                    _warnedMissingCamera = true;
                }
                return;
            }

            _warnedMissingCamera = false;
            _boundCamera = target;
            EnsureRig();
            EnsureStyles();
            AdvanceAlign(target);
            if (!SyncRig(target))
            {
                _gizmoCamera.enabled = false;
                HideLabels();
                return;
            }

            if (PollPointer())
                SyncRig(target);

            UpdateAxisFade(target);
            RebuildDraws();
            UpdateUiLabels();
        }

        void OnRenderObject()
        {
            if (_gizmoCamera == null || Camera.current != _gizmoCamera || _material == null)
                return;

            for (int i = 0; i < _draws.Count; i++)
            {
                GizmoDraw draw = _draws[i];
                if (draw.Mesh == null)
                    continue;

                _material.SetColor("_Color", draw.Color);
                if (_material.SetPass(0))
                    Graphics.DrawMeshNow(draw.Mesh, draw.Matrix);
            }
        }

        void OnGUI()
        {
            if (!Application.isPlaying || _displayActive || _gizmoCamera == null || !_gizmoCamera.enabled || _boundCamera == null)
                return;

            if (Event.current == null || Event.current.type != EventType.Repaint)
                return;

            EnsureStyles();
            LayoutProjection(out Rect backRect, out Rect modeRect);
            Vector2 pointer = _hasPointer ? _pointerView : new Vector2(-100000f, -100000f);
            DrawLabels(pointer, backRect, modeRect);
        }

        bool PollPointer()
        {
            Pointer pointer = Pointer.current;
            if (pointer == null)
            {
                _hasPointer = false;
                _hoveredPart = -1;
                return false;
            }

            Vector2 screen = pointer.position.ReadValue();
            if (!TryScreenToView(screen, out _pointerView) || PointerBlocked(screen))
            {
                _hasPointer = false;
                _hoveredPart = -1;
                return false;
            }

            _hasPointer = true;
            LayoutProjection(out Rect backRect, out Rect modeRect);
            bool acted = pointer.press.wasPressedThisFrame && TryHandleClick(_pointerView, backRect, modeRect);
            Pick(_pointerView);
            return acted;
        }

        void EnsureRig()
        {
            if (_rig != null)
                return;

            _rig = new GameObject("Scene View Gizmo Camera");
            _rig.hideFlags = HideFlags.HideAndDontSave;
            _rig.transform.SetParent(transform, false);
            _gizmoCamera = _rig.AddComponent<Camera>();
            _gizmoCamera.enabled = false;
        }

        bool SyncRig(Camera target)
        {
            _gizmoCamera.clearFlags = CameraClearFlags.Depth;
            _gizmoCamera.cullingMask = 0;
            _gizmoCamera.renderingPath = RenderingPath.Forward;
            _gizmoCamera.allowHDR = target.allowHDR;
            _gizmoCamera.allowMSAA = target.allowMSAA;
            _gizmoCamera.allowDynamicResolution = false;
            _gizmoCamera.useOcclusionCulling = false;
            _gizmoCamera.usePhysicalProperties = false;
            _gizmoCamera.depthTextureMode = DepthTextureMode.None;
            _gizmoCamera.stereoTargetEye = StereoTargetEyeMask.None;
            _gizmoCamera.targetTexture = target.targetTexture;
            _gizmoCamera.targetDisplay = target.targetDisplay;
            _gizmoCamera.depth = target.depth + 1f;
            _gizmoCamera.nearClipPlane = 0.05f;
            _gizmoCamera.farClipPlane = 50f;

            PrepareDisplay(target);
            Rect view = TargetViewRect(target);
            if (view.width < 2f || view.height < 2f)
                return false;

            _gizmoViewRect = GizmoPixelRect(view);
            ApplyGizmoViewport(target);
            Quaternion rotation = target.transform.rotation;
            float halfExtent = SceneViewGizmoGeometry.Reach * SceneViewGizmoGeometry.FitPadding;
            if (target.orthographic)
            {
                _gizmoCamera.orthographic = true;
                _gizmoCamera.orthographicSize = halfExtent;
                _gizmoCamera.transform.SetPositionAndRotation(rotation * new Vector3(0f, 0f, -4f), rotation);
            }
            else
            {
                _gizmoCamera.orthographic = false;
                _gizmoCamera.fieldOfView = SceneViewGizmoGeometry.GizmoFov;
                float distance = halfExtent / Mathf.Tan(SceneViewGizmoGeometry.GizmoFov * 0.5f * Mathf.Deg2Rad);
                _gizmoCamera.transform.SetPositionAndRotation(rotation * new Vector3(0f, 0f, -distance), rotation);
                _gizmoCamera.farClipPlane = distance + 5f;
            }

            _gizmoCamera.enabled = true;
            return true;
        }

        void AdvanceAlign(Camera target)
        {
            if (!_aligning)
                return;
            if (_alignCamera != target)
            {
                _aligning = false;
                return;
            }

            _alignT += Time.deltaTime / Mathf.Max(0.0001f, _alignDuration);
            ApplyAlign(Smooth01(_alignT));
            if (_alignT >= 1f)
                _aligning = false;
        }

        void ApplyAlign(float t)
        {
            if (_alignCamera == null)
                return;

            Transform xform = _alignCamera.transform;
            xform.rotation = Quaternion.Slerp(_alignFromRotation, _alignToRotation, t);
            xform.position = Vector3.Lerp(_alignFromPosition, _alignToPosition, t);
        }

        void UpdateAxisFade(Camera target)
        {
            Vector3 forward = target.transform.forward;
            float step = SceneViewGizmoGeometry.FadeDuration <= 0f
                ? 1f
                : Time.deltaTime / SceneViewGizmoGeometry.FadeDuration;

            for (int i = 0; i < Axes.Length; i++)
            {
                float alignment = Mathf.Abs(Vector3.Dot(Axes[i], forward));
                if (alignment >= SceneViewGizmoGeometry.FadeOutDot)
                    _axisAlphaTarget[i] = 0f;
                else if (alignment <= SceneViewGizmoGeometry.FadeInDot)
                    _axisAlphaTarget[i] = 1f;

                _axisAlpha[i] = Mathf.MoveTowards(_axisAlpha[i], _axisAlphaTarget[i], step);
            }
        }

        void RebuildDraws()
        {
            _draws.Clear();
            if (_gizmoCamera == null)
                return;

            Matrix4x4 center = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * SceneViewGizmoGeometry.CubeSize);
            AddDraw(SceneViewGizmoGeometry.Cube, center, _hoveredPart == CenterPart ? _hoveredColor : _centerColor, Vector3.zero);

            for (int i = 0; i < Axes.Length; i++)
            {
                if (_axisAlpha[i] < VisibleAlpha)
                    continue;

                Color color = i == _hoveredPart ? _hoveredColor : AxisColor(i);
                color.a *= _axisAlpha[i];
                Matrix4x4 matrix = AxisMatrix(Axes[i]);
                Vector3 midPoint = matrix.MultiplyPoint(new Vector3(0f, 0.5f, 0f));
                AddDraw(SceneViewGizmoGeometry.Cone, matrix, color, midPoint);
            }

            for (int i = 1; i < _draws.Count; i++)
            {
                GizmoDraw item = _draws[i];
                int j = i - 1;
                while (j >= 0 && _draws[j].Depth < item.Depth)
                {
                    _draws[j + 1] = _draws[j];
                    j--;
                }

                _draws[j + 1] = item;
            }
        }

        void AddDraw(Mesh mesh, Matrix4x4 matrix, Color color, Vector3 worldPoint)
        {
            float depth = Vector3.Dot(worldPoint - _gizmoCamera.transform.position, _gizmoCamera.transform.forward);
            _draws.Add(new GizmoDraw
            {
                Mesh = mesh,
                Matrix = matrix,
                Color = color,
                Depth = depth
            });
        }

        bool TryHandleClick(Vector2 guiPosition, Rect backRect, Rect modeRect)
        {
            if (_showProjectionLabel && backRect.Contains(guiPosition))
            {
                RestorePreviousView();
                return true;
            }

            if (_showProjectionLabel && modeRect.Contains(guiPosition))
            {
                ToggleProjection();
                return true;
            }

            int part = Pick(guiPosition);
            if (part < 0)
                return false;
            if (part == CenterPart)
                ToggleProjection();
            else
                AlignTo(Axes[part]);
            return true;
        }

        int Pick(Vector2 viewPosition)
        {
            Rect pixel = _gizmoViewRect;
            if (pixel.width < 1f || pixel.height < 1f)
            {
                _hoveredPart = -1;
                return -1;
            }

            float pad = 8f * _viewPerScreen;
            Rect hit = pixel;
            hit.x -= pad;
            hit.y -= pad;
            hit.width += pad * 2f;
            hit.height += pad * 2f;
            if (!hit.Contains(viewPosition))
            {
                _hoveredPart = -1;
                return -1;
            }

            float vx = (viewPosition.x - pixel.xMin) / pixel.width;
            float vy = (viewPosition.y - pixel.yMin) / pixel.height;
            Ray ray = _gizmoCamera.ViewportPointToRay(new Vector3(vx, vy, 0f));
            float best = float.PositiveInfinity;
            int bestPart = -1;
            for (int i = 0; i < Axes.Length; i++)
            {
                if (_axisAlpha[i] < VisibleAlpha)
                    continue;

                Matrix4x4 pick = AxisMatrix(Axes[i]) * Matrix4x4.Scale(new Vector3(1.15f, 1.08f, 1.15f));
                float distance;
                if (SceneViewGizmoGeometry.Raycast(ray, pick, true, out distance) && distance < best)
                {
                    best = distance;
                    bestPart = i;
                }
            }

            Matrix4x4 center = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * (SceneViewGizmoGeometry.CubeSize * 1.08f));
            float centerDistance;
            if (SceneViewGizmoGeometry.Raycast(ray, center, false, out centerDistance) && centerDistance < best)
                bestPart = CenterPart;

            _hoveredPart = bestPart;
            return bestPart;
        }

        void DrawLabels(Vector2 viewPointer, Rect backRect, Rect modeRect)
        {
            Matrix4x4 guiMatrix = GUI.matrix;
            Color guiColor = GUI.color;
            GUI.matrix = Matrix4x4.identity;

            for (int i = 0; i < AxisLabels.Length; i++)
            {
                if (_axisAlpha[i] < VisibleAlpha || !TryAxisLabelPoint(i, out Vector2 labelView))
                    continue;

                Vector2 gui = ViewToGui(labelView);
                float box = _axisStyle.fontSize + 8f;
                Rect rect = new Rect(gui.x - box * 0.5f, gui.y - box * 0.5f, box, box);
                Color color = Color.white;
                color.a = _axisAlpha[i];
                DrawText(rect, AxisLabels[i], _axisStyle, color);
            }

            if (_showProjectionLabel)
            {
                bool backHovered = backRect.Contains(viewPointer);
                bool modeHovered = modeRect.Contains(viewPointer);
                Color backColor = _hasPrevious ? new Color(1f, 1f, 1f, 0.92f) : new Color(1f, 1f, 1f, 0.28f);
                if (backHovered && _hasPrevious)
                    backColor = _hoveredColor;
                Color modeColor = modeHovered ? Color.white : new Color(1f, 1f, 1f, 0.8f);
                DrawText(ViewRectToGui(backRect), "<", _projectionStyle, backColor);
                string mode = _boundCamera.orthographic ? "Ortho" : "Persp";
                DrawText(ViewRectToGui(modeRect), mode, _projectionStyle, modeColor);
            }

            GUI.color = guiColor;
            GUI.matrix = guiMatrix;
        }

        void LayoutProjection(out Rect backRect, out Rect modeRect)
        {
            backRect = new Rect();
            modeRect = new Rect();
            if (!_showProjectionLabel)
                return;

            Rect pixel = _gizmoViewRect;
            float height = LabelHeight() * _viewPerScreen;
            Rect row = new Rect(pixel.x, pixel.yMin - height, pixel.width, height);
            string mode = _boundCamera.orthographic ? "Ortho" : "Persp";
            Vector2 modeSize = _projectionStyle.CalcSize(new GUIContent(mode)) * _viewPerScreen;
            float backWidth = (_projectionStyle.CalcSize(new GUIContent("<")).x + 8f) * _viewPerScreen;
            float gap = 4f * _viewPerScreen;
            float total = backWidth + gap + modeSize.x;
            float x = row.center.x - total * 0.5f;
            float y = row.y + (row.height - modeSize.y) * 0.5f;
            backRect = new Rect(x, y, backWidth, modeSize.y);
            modeRect = new Rect(x + backWidth + gap, y, modeSize.x, modeSize.y);
        }

        Rect GizmoPixelRect(Rect view)
        {
            float size = Mathf.Clamp(_screenSize, 48f, 512f) * _viewPerScreen;
            size = Mathf.Min(size, Mathf.Min(view.width, view.height) * 0.85f);
            Vector2 offset = _screenOffset * _viewPerScreen;
            bool bottom = _corner == SceneViewGizmoCorner.BottomLeft || _corner == SceneViewGizmoCorner.BottomRight;
            float lift = bottom ? LabelHeight() * _viewPerScreen : 0f;
            float x;
            float y;
            switch (_corner)
            {
                case SceneViewGizmoCorner.TopLeft:
                    x = view.xMin + offset.x;
                    y = view.yMax - size + offset.y;
                    break;
                case SceneViewGizmoCorner.BottomRight:
                    x = view.xMax - size + offset.x;
                    y = view.yMin + lift + offset.y;
                    break;
                case SceneViewGizmoCorner.BottomLeft:
                    x = view.xMin + offset.x;
                    y = view.yMin + lift + offset.y;
                    break;
                default:
                    x = view.xMax - size + offset.x;
                    y = view.yMax - size + offset.y;
                    break;
            }

            x = Mathf.Clamp(x, view.xMin, Mathf.Max(view.xMin, view.xMax - size));
            y = Mathf.Clamp(y, view.yMin, Mathf.Max(view.yMin, view.yMax - size));
            return new Rect(x, y, size, size);
        }

        void PrepareDisplay(Camera target)
        {
            RenderTexture texture = target.targetTexture;
            RectTransform display = Display;
            _displayActive = display != null && display.gameObject.activeInHierarchy && texture != null;
            _viewPerScreen = 1f;
            _uvRect = new Rect(0f, 0f, 1f, 1f);
            _eventCamera = null;
            if (texture != null)
            {
                _textureWidth = texture.width;
                _textureHeight = texture.height;
            }

            if (!_displayActive)
                return;

            RawImage image = display.GetComponent<RawImage>();
            if (image != null && image.uvRect.width > 0.0001f && image.uvRect.height > 0.0001f)
                _uvRect = image.uvRect;
            if (image != null && image.texture != texture && !_warnedDisplayTexture)
            {
                Debug.LogWarning("Display 上的 RawImage 没有使用目标相机的 RenderTexture，Gizmo 的点击会对不齐。", display);
                _warnedDisplayTexture = true;
            }

            _eventCamera = EventCamera(display);
            _displayScreenWidth = DisplayScreenWidth(display, _eventCamera);
            Rect view = TargetViewRect(target);
            if (_displayScreenWidth > 1f)
                _viewPerScreen = view.width / _displayScreenWidth;
        }

        void ApplyGizmoViewport(Camera target)
        {
            if (target.targetTexture != null)
            {
                float width = target.targetTexture.width;
                float height = target.targetTexture.height;
                _gizmoCamera.rect = new Rect(
                    _gizmoViewRect.x / width,
                    _gizmoViewRect.y / height,
                    _gizmoViewRect.width / width,
                    _gizmoViewRect.height / height);
                return;
            }

            _gizmoCamera.pixelRect = _gizmoViewRect;
        }

        static Rect TargetViewRect(Camera target)
        {
            if (target.targetTexture != null)
            {
                Rect normalized = target.rect;
                float width = target.targetTexture.width;
                float height = target.targetTexture.height;
                return new Rect(normalized.x * width, normalized.y * height, normalized.width * width, normalized.height * height);
            }

            return target.pixelRect;
        }

        bool TryScreenToView(Vector2 screen, out Vector2 view)
        {
            view = screen;
            if (!_displayActive)
                return true;

            if (_uvRect.width <= 0.0001f || _uvRect.height <= 0.0001f)
                return false;
            RectTransform display = Display;
            if (display == null)
                return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(display, screen, _eventCamera, out Vector2 local))
                return false;

            Rect rect = display.rect;
            if (rect.width < 1f || rect.height < 1f)
                return false;

            float u = (local.x - rect.xMin) / rect.width;
            float v = (local.y - rect.yMin) / rect.height;
            if (u < 0f || u > 1f || v < 0f || v > 1f)
                return false;

            float texU = _uvRect.x + u * _uvRect.width;
            float texV = _uvRect.y + v * _uvRect.height;
            view = new Vector2(texU * _textureWidth, texV * _textureHeight);
            return true;
        }

        bool ViewToLocal(Vector2 view, out Vector2 local)
        {
            local = Vector2.zero;
            RectTransform display = Display;
            if (display == null || _textureWidth < 1f || _textureHeight < 1f || _uvRect.width <= 0.0001f || _uvRect.height <= 0.0001f)
                return false;

            float texU = view.x / _textureWidth;
            float texV = view.y / _textureHeight;
            float u = (texU - _uvRect.x) / _uvRect.width;
            float v = (texV - _uvRect.y) / _uvRect.height;
            Rect rect = display.rect;
            local = new Vector2(
                Mathf.LerpUnclamped(rect.xMin, rect.xMax, u),
                Mathf.LerpUnclamped(rect.yMin, rect.yMax, v));
            return true;
        }

        Vector2 ViewToScreen(Vector2 view)
        {
            if (!_displayActive || !ViewToLocal(view, out Vector2 local))
                return view;

            Vector3 world = Display.TransformPoint(new Vector3(local.x, local.y, 0f));
            return RectTransformUtility.WorldToScreenPoint(_eventCamera, world);
        }

        Vector2 ViewToGui(Vector2 view)
        {
            Vector2 screen = ViewToScreen(view);
            return new Vector2(screen.x, Screen.height - screen.y);
        }

        Rect ViewRectToGui(Rect viewRect)
        {
            Vector2 min = ViewToScreen(new Vector2(viewRect.xMin, viewRect.yMin));
            Vector2 max = ViewToScreen(new Vector2(viewRect.xMax, viewRect.yMax));
            float xMin = Mathf.Min(min.x, max.x);
            float xMax = Mathf.Max(min.x, max.x);
            float yTop = Screen.height - Mathf.Max(min.y, max.y);
            float yBottom = Screen.height - Mathf.Min(min.y, max.y);
            return Rect.MinMaxRect(xMin, yTop, xMax, yBottom);
        }

        Vector2 ViewportToView(Vector3 viewport)
        {
            return new Vector2(
                _gizmoViewRect.x + viewport.x * _gizmoViewRect.width,
                _gizmoViewRect.y + viewport.y * _gizmoViewRect.height);
        }

        bool TryAxisLabelPoint(int index, out Vector2 labelView)
        {
            labelView = Vector2.zero;
            Vector3 tip = Axes[index] * (SceneViewGizmoGeometry.CubeSize * 0.5f + SceneViewGizmoGeometry.Gap + SceneViewGizmoGeometry.ConeHeight);
            Vector3 tipViewport = _gizmoCamera.WorldToViewportPoint(tip);
            if (tipViewport.z <= 0f)
                return false;

            Vector2 tipView = ViewportToView(tipViewport);
            Vector2 centerView = ViewportToView(_gizmoCamera.WorldToViewportPoint(Vector3.zero));
            Vector2 outward = tipView - centerView;
            if (outward.sqrMagnitude > 1f)
                outward.Normalize();

            float nudge = Mathf.Clamp(_screenSize * 0.09f, 8f, 18f) * _viewPerScreen;
            labelView = tipView + outward * nudge;
            return true;
        }

        bool PointerBlocked(Vector2 screen)
        {
            if (!_displayActive || EventSystem.current == null)
                return false;

            if (_uiPointer == null)
                _uiPointer = new PointerEventData(EventSystem.current);
            _uiPointer.position = screen;

            _uiHits.Clear();
            EventSystem.current.RaycastAll(_uiPointer, _uiHits);
            if (_uiHits.Count == 0)
                return false;

            GameObject top = _uiHits[0].gameObject;
            RectTransform display = Display;
            if (top == null || display == null)
                return false;

            Transform hit = top.transform;
            return hit != display && !hit.IsChildOf(display);
        }

        static Camera EventCamera(RectTransform display)
        {
            Canvas canvas = display.GetComponentInParent<Canvas>();
            if (canvas == null)
                return null;

            Canvas root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            if (root.renderMode == RenderMode.ScreenSpaceOverlay)
                return null;

            return root.worldCamera != null ? root.worldCamera : Camera.main;
        }

        static float DisplayScreenWidth(RectTransform display, Camera eventCamera)
        {
            Vector3[] corners = new Vector3[4];
            display.GetWorldCorners(corners);
            Vector2 left = RectTransformUtility.WorldToScreenPoint(eventCamera, corners[0]);
            Vector2 right = RectTransformUtility.WorldToScreenPoint(eventCamera, corners[3]);
            return Vector2.Distance(left, right);
        }

        void HideLabels()
        {
            if (_labelRoot != null)
                _labelRoot.gameObject.SetActive(false);
        }

        void UpdateUiLabels()
        {
            if (!_displayActive)
            {
                HideLabels();
                return;
            }

            EnsureLabelRoot();
            LayoutProjection(out Rect backRect, out Rect modeRect);
            PlaceAxisLabel(_labelX, 0);
            PlaceAxisLabel(_labelY, 1);
            PlaceAxisLabel(_labelZ, 2);

            bool showProjection = _showProjectionLabel;
            _labelBack.gameObject.SetActive(showProjection);
            _labelMode.gameObject.SetActive(showProjection);
            if (!showProjection)
                return;

            bool backHovered = _hasPointer && backRect.Contains(_pointerView);
            bool modeHovered = _hasPointer && modeRect.Contains(_pointerView);
            Color backColor = _hasPrevious ? new Color(1f, 1f, 1f, 0.92f) : new Color(1f, 1f, 1f, 0.28f);
            if (backHovered && _hasPrevious)
                backColor = _hoveredColor;
            Color modeColor = modeHovered ? Color.white : new Color(1f, 1f, 1f, 0.8f);
            PlaceText(_labelBack, backRect, "<", backColor, _projectionStyle.fontSize);
            PlaceText(_labelMode, modeRect, _boundCamera.orthographic ? "Ortho" : "Persp", modeColor, _projectionStyle.fontSize);
        }

        void PlaceAxisLabel(Text text, int index)
        {
            if (_axisAlpha[index] < VisibleAlpha || !TryAxisLabelPoint(index, out Vector2 labelView))
            {
                text.gameObject.SetActive(false);
                return;
            }

            float box = _axisStyle.fontSize + 8f;
            float viewBox = box * _viewPerScreen;
            Rect viewRect = new Rect(labelView.x - viewBox * 0.5f, labelView.y - viewBox * 0.5f, viewBox, viewBox);
            Color color = Color.white;
            color.a = _axisAlpha[index];
            text.gameObject.SetActive(true);
            PlaceText(text, viewRect, AxisLabels[index], color, _axisStyle.fontSize);
        }

        void PlaceText(Text text, Rect viewRect, string value, Color color, int screenFontSize)
        {
            if (!ViewToLocal(viewRect.center, out Vector2 local))
            {
                text.gameObject.SetActive(false);
                return;
            }

            RectTransform display = Display;
            if (display == null)
            {
                text.gameObject.SetActive(false);
                return;
            }

            float unitsPerScreen = _displayScreenWidth > 1f ? display.rect.width / _displayScreenWidth : 1f;
            float screenWidth = viewRect.width / Mathf.Max(0.0001f, _viewPerScreen);
            float screenHeight = viewRect.height / Mathf.Max(0.0001f, _viewPerScreen);
            RectTransform rect = text.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(Mathf.Max(8f, screenWidth * unitsPerScreen), Mathf.Max(8f, screenHeight * unitsPerScreen));
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            rect.position = display.TransformPoint(new Vector3(local.x, local.y, 0f));
            text.fontSize = Mathf.Clamp(Mathf.RoundToInt(screenFontSize * unitsPerScreen), 8, 72);
            text.text = value;
            text.color = color;
        }

        void EnsureLabelRoot()
        {
            if (_labelRoot == null)
            {
                var rootObject = new GameObject("Scene View Gizmo Labels");
                rootObject.hideFlags = HideFlags.DontSave;
                _labelRoot = rootObject.AddComponent<RectTransform>();
                var layout = rootObject.AddComponent<LayoutElement>();
                layout.ignoreLayout = true;
                _labelX = CreateLabel("X");
                _labelY = CreateLabel("Y");
                _labelZ = CreateLabel("Z");
                _labelBack = CreateLabel("Back");
                _labelMode = CreateLabel("Mode");
            }

            RectTransform display = Display;
            if (display == null)
                return;

            if (_labelRoot.parent != display)
            {
                _labelRoot.SetParent(display, false);
                _labelRoot.anchorMin = Vector2.zero;
                _labelRoot.anchorMax = Vector2.one;
                _labelRoot.offsetMin = Vector2.zero;
                _labelRoot.offsetMax = Vector2.zero;
                _labelRoot.pivot = new Vector2(0.5f, 0.5f);
                _labelRoot.localScale = Vector3.one;
                _labelRoot.localRotation = Quaternion.identity;
            }

            _labelRoot.SetAsLastSibling();
            _labelRoot.gameObject.SetActive(true);
        }

        Text CreateLabel(string name)
        {
            var labelObject = new GameObject(name);
            labelObject.hideFlags = HideFlags.DontSave;
            labelObject.transform.SetParent(_labelRoot, false);
            var text = labelObject.AddComponent<Text>();
            text.font = _axisStyle != null ? _axisStyle.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.alignment = TextAnchor.MiddleCenter;
            text.fontStyle = FontStyle.Bold;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.color = Color.white;
            var shadow = labelObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.55f);
            shadow.effectDistance = new Vector2(1f, -1f);
            shadow.useGraphicAlpha = true;
            return text;
        }

        float LabelHeight()
        {
            if (!_showProjectionLabel)
                return 0f;
            return Mathf.Clamp(_screenSize * 0.22f, 18f, 32f);
        }

        void EnsureStyles()
        {
            if (_axisStyle == null)
                _axisStyle = CreateTextStyle();
            if (_projectionStyle == null)
                _projectionStyle = CreateTextStyle();

            _axisStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(_screenSize * 0.15f), 11, 28);
            _projectionStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(_screenSize * 0.13f), 11, 22);
        }

        static GUIStyle CreateTextStyle()
        {
            var style = new GUIStyle();
            style.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            style.alignment = TextAnchor.MiddleCenter;
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = Color.white;
            return style;
        }

        static void DrawText(Rect rect, string text, GUIStyle style, Color color)
        {
            Color shadow = new Color(0f, 0f, 0f, color.a * 0.55f);
            GUI.color = shadow;
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);
            GUI.color = color;
            GUI.Label(rect, text, style);
        }

        Color AxisColor(int index)
        {
            if (index >= 3)
                return _negativeAxis;
            if (index == 0)
                return _positiveX;
            if (index == 1)
                return _positiveY;
            return _positiveZ;
        }

        static Matrix4x4 AxisMatrix(Vector3 axis)
        {
            Quaternion rotation = Quaternion.FromToRotation(Vector3.up, -axis);
            float outer = SceneViewGizmoGeometry.CubeSize * 0.5f + SceneViewGizmoGeometry.Gap + SceneViewGizmoGeometry.ConeHeight;
            return Matrix4x4.TRS(
                axis * outer,
                rotation,
                new Vector3(SceneViewGizmoGeometry.ConeRadius, SceneViewGizmoGeometry.ConeHeight, SceneViewGizmoGeometry.ConeRadius));
        }

        Camera ResolveCamera()
        {
            if (IsSceneCamera(_targetCamera))
                return _targetCamera;

            Camera attached = GetComponent<Camera>();
            if (IsSceneCamera(attached))
                return attached;

            if (IsSceneCamera(Camera.main))
                return Camera.main;

            return null;
        }

        bool IsSceneCamera(Camera camera)
        {
            return camera != null && camera != _gizmoCamera && camera.enabled;
        }

        Vector3 PivotPoint(Camera camera)
        {
            if (Pivot != null)
                return Pivot.position;
            return camera.transform.position + camera.transform.forward * Mathf.Max(0.01f, _orbitDistance);
        }

        static Quaternion RotationLookingAlong(Transform cameraTransform, Vector3 forward)
        {
            Vector3 up = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.99f)
            {
                up = Vector3.ProjectOnPlane(cameraTransform.up, forward);
                if (up.sqrMagnitude < 1e-4f)
                    up = Vector3.ProjectOnPlane(cameraTransform.forward, forward);
                if (up.sqrMagnitude < 1e-4f)
                    up = Vector3.forward;
                up.Normalize();
            }

            return Quaternion.LookRotation(forward, up);
        }

        void Remember(Camera camera)
        {
            _previousPose = Capture(camera);
            _hasPrevious = true;
        }

        static CameraPose Capture(Camera camera)
        {
            return new CameraPose
            {
                Position = camera.transform.position,
                Rotation = camera.transform.rotation,
                Orthographic = camera.orthographic,
                OrthographicSize = camera.orthographicSize,
                FieldOfView = camera.fieldOfView
            };
        }

        static void ApplyPose(Camera camera, CameraPose pose)
        {
            camera.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            camera.orthographic = pose.Orthographic;
            camera.orthographicSize = pose.OrthographicSize;
            camera.fieldOfView = pose.FieldOfView;
        }

        Material CreateMaterial()
        {
            Material source = Resources.Load<Material>("SceneViewGizmoSolid");
            Material material = null;
            if (source != null)
                material = new Material(source);
            else
            {
                Shader shader = Shader.Find("Hidden/SceneViewGizmo/Solid");
                if (shader == null)
                    shader = Shader.Find("Sprites/Default");
                if (shader != null)
                    material = new Material(shader);
            }

            if (material == null)
            {
                if (!_warnedMissingMaterial)
                {
                    Debug.LogError("Scene View Gizmo 找不到着色器 Hidden/SceneViewGizmo/Solid。", this);
                    _warnedMissingMaterial = true;
                }
                return null;
            }

            material.hideFlags = HideFlags.HideAndDontSave;
            material.enableInstancing = false;
            return material;
        }

        static float Smooth01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
