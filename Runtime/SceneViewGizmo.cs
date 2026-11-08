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
        public Transform _pivot;
        [Tooltip("勾选后，点击坐标轴会直接转动目标相机。")]
        [SerializeField] bool _controlCamera = true;
        [SerializeField] SceneViewGizmoAlignMode _alignMode = SceneViewGizmoAlignMode.Orbit;
        [Tooltip("没有指定枢轴时，相机与观察点的距离。")]
        [SerializeField, Min(0.01f)] float _orbitDistance = 5f;
        [Tooltip("对齐动画时长，单位秒。0 表示立刻对齐。")]
        [SerializeField, Min(0f)] float _alignDuration = 0.35f;

        [Header("位置")]
        [Tooltip("相机渲染到 RenderTexture 时，指定显示这块纹理的界面，一般是 RawImage。坐标轴画进纹理，文字和点击跟这块区域对齐。留空则直接画在屏幕上。")]
        public RectTransform _display;
        [Tooltip("生成的坐标轴相机的 Culling Mask，和普通相机的 LayerMask 一样。默认 Nothing，只绘制坐标轴本身。勾选 Layer 后，坐标轴画在第一个勾选的 Layer 上，这台相机只渲染这些 Layer。")]
        public LayerMask _cullingMask;
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

        [Header("地面网格")]
        [Tooltip("在目标相机画面里绘制 XZ 地面网格。平面盖住相机视锥，格子按到网格平面的距离在 10 倍尺寸之间淡入淡出。")]
        public bool _drawGrid;
        [Tooltip("网格原点。平面经过这个点，格子线按这个点对齐，默认是世界原点。")]
        public Vector3 _gridOrigin;
        [SerializeField, Min(0.01f)] float _gridCellSize = 1f;
        [SerializeField] Color _gridLineColor = new Color(128f / 255f, 128f / 255f, 128f / 255f, 102f / 255f);

        struct GizmoDraw
        {
            public Mesh Mesh;
            public Matrix4x4 Matrix;
            public Color Color;
            public float Depth;
        }

        public struct CameraPose
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
        Material _gridMaterial;
        Material _blitMaterial;
        static Mesh _gridQuad;
        RenderTexture _modelTexture;
        MaterialPropertyBlock _colorBlock;
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
        float _viewScale = 1f;
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

        public virtual Camera TargetCamera
        {
            get { return _targetCamera; }
            set { _targetCamera = value; }
        }

        public virtual Transform Pivot
        {
            get { return _pivot; }
            set { _pivot = value; }
        }

        public virtual bool ControlCamera
        {
            get { return _controlCamera; }
            set { _controlCamera = value; }
        }

        public virtual SceneViewGizmoAlignMode AlignMode
        {
            get { return _alignMode; }
            set { _alignMode = value; }
        }

        public virtual float OrbitDistance
        {
            get { return _orbitDistance; }
            set { _orbitDistance = value; }
        }

        public virtual float AlignDuration
        {
            get { return _alignDuration; }
            set { _alignDuration = value; }
        }

        public virtual RectTransform Display
        {
            get { return _display; }
            set { _display = value; }
        }

        public virtual LayerMask CullingMask
        {
            get { return _cullingMask; }
            set { _cullingMask = value; }
        }

        public virtual SceneViewGizmoCorner Corner
        {
            get { return _corner; }
            set { _corner = value; }
        }

        public virtual float ScreenSize
        {
            get { return _screenSize; }
            set { _screenSize = value; }
        }

        public virtual Vector2 ScreenOffset
        {
            get { return _screenOffset; }
            set { _screenOffset = value; }
        }

        public virtual Color PositiveX
        {
            get { return _positiveX; }
            set { _positiveX = value; }
        }

        public virtual Color PositiveY
        {
            get { return _positiveY; }
            set { _positiveY = value; }
        }

        public virtual Color PositiveZ
        {
            get { return _positiveZ; }
            set { _positiveZ = value; }
        }

        public virtual Color NegativeAxis
        {
            get { return _negativeAxis; }
            set { _negativeAxis = value; }
        }

        public virtual Color CenterColor
        {
            get { return _centerColor; }
            set { _centerColor = value; }
        }

        public virtual Color HoveredColor
        {
            get { return _hoveredColor; }
            set { _hoveredColor = value; }
        }

        public virtual bool ShowProjectionLabel
        {
            get { return _showProjectionLabel; }
            set { _showProjectionLabel = value; }
        }

        public virtual bool DrawGrid
        {
            get { return _drawGrid; }
            set { _drawGrid = value; }
        }

        public virtual Vector3 GridOrigin
        {
            get { return _gridOrigin; }
            set { _gridOrigin = value; }
        }

        public virtual float GridCellSize
        {
            get { return _gridCellSize; }
            set { _gridCellSize = Mathf.Max(0.01f, value); }
        }

        public virtual Color GridLineColor
        {
            get { return _gridLineColor; }
            set { _gridLineColor = value; }
        }

        public event Action<Vector3> AxisClicked;
        public event Action ProjectionClicked;

        public virtual void AlignTo(Vector3 worldAxis)
        {
            if (worldAxis.sqrMagnitude < 1e-8f)
                return;

            Vector3 axis = worldAxis.normalized;
            if (AxisClicked != null)
                AxisClicked(axis);
            if (!ControlCamera)
                return;

            Camera camera = ResolveCamera();
            if (camera == null)
                return;

            if (!_aligning)
                Remember(camera);

            Quaternion targetRotation = RotationLookingAlong(camera.transform, -axis);
            Vector3 targetPosition = camera.transform.position;
            if (AlignMode == SceneViewGizmoAlignMode.Orbit)
            {
                Vector3 pivot = PivotPoint(camera);
                float distance = Vector3.Distance(camera.transform.position, pivot);
                if (distance < 0.01f)
                    distance = Mathf.Max(0.01f, OrbitDistance);
                targetPosition = pivot - targetRotation * Vector3.forward * distance;
            }

            _alignCamera = camera;
            _alignFromRotation = camera.transform.rotation;
            _alignToRotation = targetRotation;
            _alignFromPosition = camera.transform.position;
            _alignToPosition = targetPosition;
            _alignT = 0f;
            _aligning = AlignDuration > 0f;
            if (!_aligning)
                ApplyAlign(1f);
        }

        public virtual void ToggleProjection()
        {
            if (ProjectionClicked != null)
                ProjectionClicked();
            if (!ControlCamera)
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
                : Mathf.Max(0.01f, OrbitDistance);
            camera.orthographicSize = Mathf.Max(0.01f, distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
            camera.orthographic = true;
        }

        public virtual void RestorePreviousView()
        {
            if (!_hasPrevious || !ControlCamera)
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
        public virtual bool ContainsScreenPoint(Vector2 screenPosition)
        {
            if (_gizmoCamera == null || !_gizmoCamera.enabled || _gizmoViewRect.width < 1f)
                return false;
            if (!TryScreenToView(screenPosition, out Vector2 view))
                return false;

            Rect pixel = _gizmoViewRect;
            float pad = 20f * _viewPerScreen * _viewScale;
            float label = LabelHeight() * _viewPerScreen;
            pixel.x -= pad;
            pixel.y -= pad + label;
            pixel.width += pad * 2f;
            pixel.height += pad * 2f + label;
            return pixel.Contains(view);
        }

        public virtual void Awake()
        {
            for (int i = 0; i < _axisAlpha.Length; i++)
            {
                _axisAlpha[i] = 1f;
                _axisAlphaTarget[i] = 1f;
            }
        }

        public virtual void OnEnable()
        {
            if (!Application.isPlaying)
                return;

            SceneViewGizmoGeometry.Warmup();
            _material = CreateMaterial();
        }

        public virtual void OnDisable()
        {
            if (_gizmoCamera != null)
                _gizmoCamera.enabled = false;
        }

        public virtual void OnDestroy()
        {
            if (_material != null)
                Destroy(_material);
            if (_gridMaterial != null)
                Destroy(_gridMaterial);
            if (_blitMaterial != null)
                Destroy(_blitMaterial);
            ReleaseModelTexture();
            if (_rig != null)
                Destroy(_rig);
            if (_labelRoot != null)
                Destroy(_labelRoot.gameObject);
        }

        public virtual void LateUpdate()
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
            SubmitMaskedModel();
            UpdateUiLabels();
        }

        public virtual void OnRenderObject()
        {
            DrawGroundGrid();
            if (_gizmoCamera == null || Camera.current != _gizmoCamera || _material == null)
                return;

            bool modelTexture = _modelTexture != null && _gizmoCamera.targetTexture == _modelTexture;
            if (!modelTexture && CullingMask.value != 0)
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

            if (modelTexture)
            {
                GL.Flush();
                CompositeModel();
            }
        }

        public virtual void DrawGroundGrid()
        {
            if (!Application.isPlaying || !DrawGrid)
                return;

            Camera camera = Camera.current;
            Camera target = _boundCamera != null ? _boundCamera : ResolveCamera();
            if (camera == null || target == null || camera != target || camera == _gizmoCamera)
                return;

            if (_gridMaterial == null)
                _gridMaterial = CreateGridMaterial();
            if (_gridMaterial == null)
                return;

            Matrix4x4 matrix = GridPlaneMatrix(camera);
            if (matrix.GetColumn(0).magnitude < 1e-4f || matrix.GetColumn(2).magnitude < 1e-4f)
                return;

            float zoom = Mathf.Abs(new Plane(Vector3.up, GridOrigin).GetDistanceToPoint(camera.transform.position));
            int digits = GridZoomDigits((int)Mathf.Clamp(zoom, 0f, 1000000f));
            float cellScale0 = Mathf.Pow(10f, digits - 1);
            float cellScale1 = Mathf.Pow(10f, digits);
            float span = cellScale1 - cellScale0;
            if (span <= 0f || float.IsInfinity(cellScale0) || float.IsInfinity(cellScale1))
                return;

            float alpha0 = (cellScale1 - zoom) / span;
            _gridMaterial.SetFloat("_CamFarPlaneDist", camera.farClipPlane);
            _gridMaterial.SetVector("_CamWorldPos", camera.transform.position);

            Mesh quad = GridQuad();
            float cell = GridCellSize;
            SubmitGridPass(quad, matrix, cell * cellScale0, GridLineColor.a * alpha0);
            SubmitGridPass(quad, matrix, cell * cellScale1, GridLineColor.a * (1f - alpha0));
        }

        public virtual void SubmitGridPass(Mesh quad, Matrix4x4 matrix, float cellSize, float alpha)
        {
            if (alpha == 0f || quad == null || _gridMaterial == null || cellSize <= 0f)
                return;

            Vector3 center = matrix.GetColumn(3);
            double ox = (center.x - GridOrigin.x) / cellSize;
            double oz = (center.z - GridOrigin.z) / cellSize;
            _gridMaterial.SetVector("_GridScale", new Vector4(matrix.GetColumn(0).magnitude, matrix.GetColumn(2).magnitude, 0f, 0f));
            _gridMaterial.SetVector("_GridFrac", new Vector4((float)(ox - Math.Floor(ox)), (float)(oz - Math.Floor(oz)), 0f, 0f));
            Color color = GridLineColor;
            color.a = alpha;
            _gridMaterial.SetFloat("_CellSizeX", cellSize);
            _gridMaterial.SetFloat("_CellSizeZ", cellSize);
            _gridMaterial.SetColor("_LineColor", color);
            if (_gridMaterial.SetPass(0))
                Graphics.DrawMeshNow(quad, matrix);
        }

        public virtual int GridZoomDigits(int number)
        {
            return number == 0 ? 1 : Mathf.FloorToInt(Mathf.Log10(Mathf.Abs(number)) + 1f);
        }

        public virtual Matrix4x4 GridPlaneMatrix(Camera camera)
        {
            if (!TryGroundFootprint(camera, out Vector3 min, out Vector3 max))
                return Matrix4x4.zero;

            float pad = Mathf.Max(GridCellSize * 2f, 0.5f);
            min.x -= pad;
            min.z -= pad;
            max.x += pad;
            max.z += pad;
            Vector3 center = (min + max) * 0.5f;
            center.y = GridOrigin.y;
            Vector3 size = max - min;
            const float maxSize = 20000f;
            Vector3 anchor = camera.transform.position;
            center.x = ClampGridSpan(center.x, anchor.x, min.x, max.x, size.x, maxSize);
            center.z = ClampGridSpan(center.z, anchor.z, min.z, max.z, size.z, maxSize);
            size.x = Mathf.Clamp(size.x, 1e-3f, maxSize);
            size.y = 1f;
            size.z = Mathf.Clamp(size.z, 1e-3f, maxSize);
            if (!IsFiniteVector(center) || !IsFiniteVector(size))
                return Matrix4x4.zero;

            return Matrix4x4.TRS(center, Quaternion.identity, size);
        }

        public virtual float ClampGridSpan(float center, float anchor, float min, float max, float size, float maxSize)
        {
            if (size <= maxSize)
                return center;

            float half = maxSize * 0.5f;
            float low = min + half;
            float high = max - half;
            if (low > high)
                return center;

            return Mathf.Clamp(Mathf.Clamp(anchor, min, max), low, high);
        }

        public virtual bool TryGroundFootprint(Camera camera, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, GridOrigin.y, float.MaxValue);
            max = new Vector3(float.MinValue, GridOrigin.y, float.MinValue);
            if (camera == null)
                return false;

            int count = 0;
            var corners = new Vector3[8];
            for (int i = 0; i < corners.Length; i++)
            {
                corners[i] = FrustumCorner(camera, i);
                if (!IsFiniteVector(corners[i]))
                    return false;
                if (Mathf.Abs(corners[i].y - GridOrigin.y) <= 0.01f)
                    EncapsulateGridPoint(new Vector3(corners[i].x, GridOrigin.y, corners[i].z), ref min, ref max, ref count);
            }

            AddGroundEdge(corners[0], corners[1], ref min, ref max, ref count);
            AddGroundEdge(corners[1], corners[2], ref min, ref max, ref count);
            AddGroundEdge(corners[2], corners[3], ref min, ref max, ref count);
            AddGroundEdge(corners[3], corners[0], ref min, ref max, ref count);
            AddGroundEdge(corners[4], corners[5], ref min, ref max, ref count);
            AddGroundEdge(corners[5], corners[6], ref min, ref max, ref count);
            AddGroundEdge(corners[6], corners[7], ref min, ref max, ref count);
            AddGroundEdge(corners[7], corners[4], ref min, ref max, ref count);
            AddGroundEdge(corners[0], corners[4], ref min, ref max, ref count);
            AddGroundEdge(corners[1], corners[5], ref min, ref max, ref count);
            AddGroundEdge(corners[2], corners[6], ref min, ref max, ref count);
            AddGroundEdge(corners[3], corners[7], ref min, ref max, ref count);
            return count >= 2 && IsFiniteVector(min) && IsFiniteVector(max);
        }

        public virtual Vector3 FrustumCorner(Camera camera, int index)
        {
            Transform xform = camera.transform;
            bool far = index >= 4;
            int side = index & 3;
            float sx = side == 0 || side == 3 ? -1f : 1f;
            float sy = side < 2 ? -1f : 1f;
            float dist = far ? camera.farClipPlane : Mathf.Max(camera.nearClipPlane, 0.01f);
            float halfH;
            float halfW;
            if (camera.orthographic)
            {
                halfH = camera.orthographicSize;
                halfW = halfH * camera.aspect;
            }
            else
            {
                halfH = dist * Mathf.Tan(Mathf.Clamp(camera.fieldOfView, 1f, 179f) * 0.5f * Mathf.Deg2Rad);
                halfW = halfH * camera.aspect;
            }

            return xform.position + xform.forward * dist + xform.right * (sx * halfW) + xform.up * (sy * halfH);
        }

        public virtual void AddGroundEdge(Vector3 a, Vector3 b, ref Vector3 min, ref Vector3 max, ref int count)
        {
            float da = a.y - GridOrigin.y;
            float db = b.y - GridOrigin.y;
            if (da * db >= 0f)
                return;

            float t = da / (da - db);
            Vector3 point = a + (b - a) * t;
            EncapsulateGridPoint(new Vector3(point.x, GridOrigin.y, point.z), ref min, ref max, ref count);
        }

        public virtual void EncapsulateGridPoint(Vector3 point, ref Vector3 min, ref Vector3 max, ref int count)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
            count++;
        }

        public virtual bool IsFiniteVector(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        public virtual Material CreateGridMaterial()
        {
            Material source = Resources.Load<Material>("SceneViewGizmoGrid");
            Material material = null;
            if (source != null)
                material = new Material(source);
            else
            {
                Shader shader = Shader.Find("Hidden/SceneViewGizmo/Grid");
                if (shader == null)
                    shader = Resources.Load<Shader>("SceneViewGizmoGrid");
                if (shader != null)
                    material = new Material(shader);
            }

            if (material == null)
            {
                Debug.LogError("Scene View Gizmo 找不到地面网格材质 SceneViewGizmoGrid。", this);
                return null;
            }

            material.hideFlags = HideFlags.HideAndDontSave;
            return material;
        }

        static Mesh GridQuad()
        {
            if (_gridQuad != null)
                return _gridQuad;

            _gridQuad = new Mesh();
            _gridQuad.name = "Scene View Grid Quad";
            _gridQuad.hideFlags = HideFlags.HideAndDontSave;
            _gridQuad.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f),
                new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, -0.5f)
            };
            _gridQuad.SetTriangles(new[] { 0, 1, 2, 2, 3, 0 }, 0);
            _gridQuad.UploadMeshData(false);
            return _gridQuad;
        }

        public virtual void OnGUI()
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

        public virtual bool PollPointer()
        {
            Pointer pointer = Pointer.current != null ? Pointer.current : Mouse.current;
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

        public virtual void EnsureRig()
        {
            if (_rig != null)
                return;

            _rig = new GameObject("Scene View Gizmo Camera");
            //_rig.hideFlags = HideFlags.HideAndDontSave;
            _rig.transform.SetParent(transform, false);
            _gizmoCamera = _rig.AddComponent<Camera>();
            _gizmoCamera.enabled = false;
        }

        public virtual bool SyncRig(Camera target)
        {
            _gizmoCamera.clearFlags = CameraClearFlags.Depth;
            _gizmoCamera.cullingMask = CullingMask.value;
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

        public virtual void AdvanceAlign(Camera target)
        {
            if (!_aligning)
                return;
            if (_alignCamera != target)
            {
                _aligning = false;
                return;
            }

            _alignT += Time.deltaTime / Mathf.Max(0.0001f, AlignDuration);
            ApplyAlign(Smooth01(_alignT));
            if (_alignT >= 1f)
                _aligning = false;
        }

        public virtual void ApplyAlign(float t)
        {
            if (_alignCamera == null)
                return;

            Transform xform = _alignCamera.transform;
            xform.rotation = Quaternion.Slerp(_alignFromRotation, _alignToRotation, t);
            xform.position = Vector3.Lerp(_alignFromPosition, _alignToPosition, t);
        }

        public virtual void UpdateAxisFade(Camera target)
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

        public virtual void RebuildDraws()
        {
            _draws.Clear();
            if (_gizmoCamera == null)
                return;

            Matrix4x4 center = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * SceneViewGizmoGeometry.CubeSize);
            AddDraw(SceneViewGizmoGeometry.Cube, center, _hoveredPart == CenterPart ? HoveredColor : CenterColor, Vector3.zero);

            for (int i = 0; i < Axes.Length; i++)
            {
                if (_axisAlpha[i] < VisibleAlpha)
                    continue;

                Color color = i == _hoveredPart ? HoveredColor : AxisColor(i);
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

        public virtual void AddDraw(Mesh mesh, Matrix4x4 matrix, Color color, Vector3 worldPoint)
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

        public virtual bool TryHandleClick(Vector2 guiPosition, Rect backRect, Rect modeRect)
        {
            if (ShowProjectionLabel && backRect.Contains(guiPosition))
            {
                RestorePreviousView();
                return true;
            }

            if (ShowProjectionLabel && modeRect.Contains(guiPosition))
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

        public virtual int Pick(Vector2 viewPosition)
        {
            Rect pixel = _gizmoViewRect;
            if (pixel.width < 1f || pixel.height < 1f)
            {
                _hoveredPart = -1;
                return -1;
            }

            float pad = 8f * _viewPerScreen * _viewScale;
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
            Ray ray = GizmoRay(vx, vy);
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

        public virtual Ray GizmoRay(float viewportX, float viewportY)
        {
            Transform xform = _gizmoCamera.transform;
            float nx = (viewportX - 0.5f) * 2f;
            float ny = (viewportY - 0.5f) * 2f;
            if (_gizmoCamera.orthographic)
            {
                float half = _gizmoCamera.orthographicSize;
                float aspect = Mathf.Max(0.01f, _gizmoCamera.aspect);
                Vector3 origin = xform.position + xform.right * (nx * half * aspect) + xform.up * (ny * half);
                return new Ray(origin, xform.forward);
            }

            float tan = Mathf.Tan(_gizmoCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float perspectiveAspect = Mathf.Max(0.01f, _gizmoCamera.aspect);
            Vector3 direction = xform.forward + xform.right * (nx * tan * perspectiveAspect) + xform.up * (ny * tan);
            return new Ray(xform.position, direction.normalized);
        }

        public virtual void DrawLabels(Vector2 viewPointer, Rect backRect, Rect modeRect)
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

            if (ShowProjectionLabel)
            {
                bool backHovered = backRect.Contains(viewPointer);
                bool modeHovered = modeRect.Contains(viewPointer);
                Color backColor = _hasPrevious ? new Color(1f, 1f, 1f, 0.92f) : new Color(1f, 1f, 1f, 0.28f);
                if (backHovered && _hasPrevious)
                    backColor = HoveredColor;
                Color modeColor = modeHovered ? Color.white : new Color(1f, 1f, 1f, 0.8f);
                DrawText(ViewRectToGui(backRect), "<", _projectionStyle, backColor);
                string mode = _boundCamera.orthographic ? "Ortho" : "Persp";
                DrawText(ViewRectToGui(modeRect), mode, _projectionStyle, modeColor);
            }

            GUI.color = guiColor;
            GUI.matrix = guiMatrix;
        }

        public virtual void LayoutProjection(out Rect backRect, out Rect modeRect)
        {
            backRect = new Rect();
            modeRect = new Rect();
            if (!ShowProjectionLabel)
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

        public virtual Rect GizmoPixelRect(Rect view)
        {
            float size = Mathf.Clamp(ScreenSize, 48f, 512f) * _viewScale * _viewPerScreen;
            size = Mathf.Min(size, Mathf.Min(view.width, view.height) * 0.85f);
            Vector2 offset = ScreenOffset * _viewScale * _viewPerScreen;
            bool bottom = Corner == SceneViewGizmoCorner.BottomLeft || Corner == SceneViewGizmoCorner.BottomRight;
            float lift = bottom ? LabelHeight() * _viewPerScreen : 0f;
            float x;
            float y;
            switch (Corner)
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

        public virtual void PrepareDisplay(Camera target)
        {
            RenderTexture texture = target.targetTexture;
            RectTransform display = Display;
            _displayActive = display != null && display.gameObject.activeInHierarchy && texture != null;
            _viewPerScreen = 1f;
            _viewScale = 1f;
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
            _viewScale = ViewScale(display);
        }

        public virtual float ViewScale(RectTransform display)
        {
            Canvas canvas = display.GetComponentInParent<Canvas>();
            Transform root = canvas != null && canvas.rootCanvas != null ? canvas.rootCanvas.transform : null;
            float canvasScale = 1f;
            if (root != null)
                canvasScale = DominantScale(root.lossyScale);
            return Mathf.Max(0.01f, DominantScale(display.lossyScale) / canvasScale);
        }

        public virtual float DominantScale(Vector3 scale)
        {
            return Mathf.Max(0.0001f, Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)));
        }

        public virtual void SubmitMaskedModel()
        {
            if (CullingMask.value == 0 || _gizmoCamera == null || !_gizmoCamera.enabled || _material == null || _gizmoCamera.targetTexture != null)
                return;

            if (_colorBlock == null)
                _colorBlock = new MaterialPropertyBlock();

            int layer = FirstLayer(CullingMask);
            for (int i = 0; i < _draws.Count; i++)
            {
                GizmoDraw draw = _draws[i];
                if (draw.Mesh == null)
                    continue;

                _colorBlock.SetColor("_Color", draw.Color);
                Graphics.DrawMesh(draw.Mesh, draw.Matrix, _material, layer, _gizmoCamera, 0, _colorBlock);
            }
        }

        public virtual int FirstLayer(LayerMask mask)
        {
            int bits = mask.value;
            for (int layer = 0; layer < 32; layer++)
            {
                if ((bits & (1 << layer)) != 0)
                    return layer;
            }

            return 0;
        }

        public virtual void ApplyGizmoViewport(Camera target)
        {
            if (target.targetTexture != null)
            {
                int width = Mathf.Max(2, Mathf.CeilToInt(_gizmoViewRect.width));
                int height = Mathf.Max(2, Mathf.CeilToInt(_gizmoViewRect.height));
                EnsureModelTexture(width, height);
                _gizmoCamera.targetTexture = _modelTexture;
                _gizmoCamera.rect = new Rect(0f, 0f, 1f, 1f);
                _gizmoCamera.clearFlags = CameraClearFlags.SolidColor;
                _gizmoCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                return;
            }

            ReleaseModelTexture();
            _gizmoCamera.targetTexture = null;
            _gizmoCamera.clearFlags = CameraClearFlags.Depth;
            _gizmoCamera.pixelRect = _gizmoViewRect;
        }

        public virtual void EnsureModelTexture(int width, int height)
        {
            if (_modelTexture != null && _modelTexture.width == width && _modelTexture.height == height)
                return;

            ReleaseModelTexture();
            _modelTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            _modelTexture.name = "Scene View Gizmo Model";
            _modelTexture.filterMode = FilterMode.Bilinear;
            _modelTexture.wrapMode = TextureWrapMode.Clamp;
            _modelTexture.hideFlags = HideFlags.HideAndDontSave;
            _modelTexture.Create();
        }

        public virtual void ReleaseModelTexture()
        {
            if (_modelTexture == null)
                return;

            _modelTexture.Release();
            Destroy(_modelTexture);
            _modelTexture = null;
        }

        public virtual void CompositeModel()
        {
            RenderTexture destination = _boundCamera != null ? _boundCamera.targetTexture : null;
            if (destination == null || _modelTexture == null)
                return;

            if (_blitMaterial == null)
                _blitMaterial = CreateBlitMaterial();
            if (_blitMaterial == null)
                return;

            Rect corner = new Rect(
                _gizmoViewRect.x,
                destination.height - _gizmoViewRect.y - _gizmoViewRect.height,
                _gizmoViewRect.width,
                _gizmoViewRect.height);
            RenderTexture previous = RenderTexture.active;
            Graphics.SetRenderTarget(destination);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, destination.width, destination.height, 0f);
            Graphics.DrawTexture(corner, _modelTexture, _blitMaterial);
            GL.PopMatrix();
            Graphics.SetRenderTarget(previous != null ? previous : _modelTexture);
        }

        public virtual Material CreateBlitMaterial()
        {
            Shader shader = Shader.Find("Hidden/SceneViewGizmo/Blit");
            if (shader == null)
                shader = Resources.Load<Shader>("SceneViewGizmoBlit");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            if (shader == null)
                return null;

            Material material = new Material(shader);
            material.hideFlags = HideFlags.HideAndDontSave;
            material.color = Color.white;
            return material;
        }

        public virtual Rect TargetViewRect(Camera target)
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

        public virtual bool TryScreenToView(Vector2 screen, out Vector2 view)
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

        public virtual bool ViewToLocal(Vector2 view, out Vector2 local)
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

        public virtual Vector2 ViewToScreen(Vector2 view)
        {
            if (!_displayActive || !ViewToLocal(view, out Vector2 local))
                return view;

            Vector3 world = Display.TransformPoint(new Vector3(local.x, local.y, 0f));
            return RectTransformUtility.WorldToScreenPoint(_eventCamera, world);
        }

        public virtual Vector2 ViewToGui(Vector2 view)
        {
            Vector2 screen = ViewToScreen(view);
            return new Vector2(screen.x, Screen.height - screen.y);
        }

        public virtual Rect ViewRectToGui(Rect viewRect)
        {
            Vector2 min = ViewToScreen(new Vector2(viewRect.xMin, viewRect.yMin));
            Vector2 max = ViewToScreen(new Vector2(viewRect.xMax, viewRect.yMax));
            float xMin = Mathf.Min(min.x, max.x);
            float xMax = Mathf.Max(min.x, max.x);
            float yTop = Screen.height - Mathf.Max(min.y, max.y);
            float yBottom = Screen.height - Mathf.Min(min.y, max.y);
            return Rect.MinMaxRect(xMin, yTop, xMax, yBottom);
        }

        public virtual Vector2 ViewportToView(Vector3 viewport)
        {
            return new Vector2(
                _gizmoViewRect.x + viewport.x * _gizmoViewRect.width,
                _gizmoViewRect.y + viewport.y * _gizmoViewRect.height);
        }

        public virtual bool TryAxisLabelPoint(int index, out Vector2 labelView)
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

            float nudge = Mathf.Clamp(ScreenSize * 0.09f, 8f, 18f) * _viewScale * _viewPerScreen;
            labelView = tipView + outward * nudge;
            return true;
        }

        public virtual bool PointerBlocked(Vector2 screen)
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

            RectTransform display = Display;
            if (display == null)
                return false;

            for (int i = 0; i < _uiHits.Count; i++)
            {
                GameObject top = _uiHits[i].gameObject;
                if (top == null)
                    continue;

                Transform hit = top.transform;
                if (hit == display || hit.IsChildOf(display) || display.IsChildOf(hit))
                    return false;

                return true;
            }

            return false;
        }

        public virtual Camera EventCamera(RectTransform display)
        {
            Canvas canvas = display.GetComponentInParent<Canvas>();
            if (canvas == null)
                return null;

            Canvas root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            if (root.renderMode == RenderMode.ScreenSpaceOverlay)
                return null;

            return root.worldCamera != null ? root.worldCamera : Camera.main;
        }

        public virtual float DisplayScreenWidth(RectTransform display, Camera eventCamera)
        {
            Vector3[] corners = new Vector3[4];
            display.GetWorldCorners(corners);
            Vector2 left = RectTransformUtility.WorldToScreenPoint(eventCamera, corners[0]);
            Vector2 right = RectTransformUtility.WorldToScreenPoint(eventCamera, corners[3]);
            return Vector2.Distance(left, right);
        }

        public virtual void HideLabels()
        {
            if (_labelRoot != null)
                _labelRoot.gameObject.SetActive(false);
        }

        public virtual void UpdateUiLabels()
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

            bool showProjection = ShowProjectionLabel;
            _labelBack.gameObject.SetActive(showProjection);
            _labelMode.gameObject.SetActive(showProjection);
            if (!showProjection)
                return;

            bool backHovered = _hasPointer && backRect.Contains(_pointerView);
            bool modeHovered = _hasPointer && modeRect.Contains(_pointerView);
            Color backColor = _hasPrevious ? new Color(1f, 1f, 1f, 0.92f) : new Color(1f, 1f, 1f, 0.28f);
            if (backHovered && _hasPrevious)
                backColor = HoveredColor;
            Color modeColor = modeHovered ? Color.white : new Color(1f, 1f, 1f, 0.8f);
            PlaceText(_labelBack, backRect, "<", backColor, _projectionStyle.fontSize);
            PlaceText(_labelMode, modeRect, _boundCamera.orthographic ? "Ortho" : "Persp", modeColor, _projectionStyle.fontSize);
        }

        public virtual void PlaceAxisLabel(Text text, int index)
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

        public virtual void PlaceText(Text text, Rect viewRect, string value, Color color, int screenFontSize)
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

        public virtual void EnsureLabelRoot()
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

        public virtual Text CreateLabel(string name)
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

        public virtual float LabelHeight()
        {
            if (!ShowProjectionLabel)
                return 0f;
            return Mathf.Clamp(ScreenSize * 0.22f, 18f, 32f) * _viewScale;
        }

        public virtual void EnsureStyles()
        {
            if (_axisStyle == null)
                _axisStyle = CreateTextStyle();
            if (_projectionStyle == null)
                _projectionStyle = CreateTextStyle();

            _axisStyle.fontSize = Mathf.Max(1, Mathf.RoundToInt(Mathf.Clamp(ScreenSize * 0.15f, 11f, 28f) * _viewScale));
            _projectionStyle.fontSize = Mathf.Max(1, Mathf.RoundToInt(Mathf.Clamp(ScreenSize * 0.13f, 11f, 22f) * _viewScale));
        }

        public virtual GUIStyle CreateTextStyle()
        {
            var style = new GUIStyle();
            style.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            style.alignment = TextAnchor.MiddleCenter;
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = Color.white;
            return style;
        }

        public virtual void DrawText(Rect rect, string text, GUIStyle style, Color color)
        {
            Color shadow = new Color(0f, 0f, 0f, color.a * 0.55f);
            GUI.color = shadow;
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);
            GUI.color = color;
            GUI.Label(rect, text, style);
        }

        public virtual Color AxisColor(int index)
        {
            if (index >= 3)
                return NegativeAxis;
            if (index == 0)
                return PositiveX;
            if (index == 1)
                return PositiveY;
            return PositiveZ;
        }

        public virtual Matrix4x4 AxisMatrix(Vector3 axis)
        {
            Quaternion rotation = Quaternion.FromToRotation(Vector3.up, -axis);
            float outer = SceneViewGizmoGeometry.CubeSize * 0.5f + SceneViewGizmoGeometry.Gap + SceneViewGizmoGeometry.ConeHeight;
            return Matrix4x4.TRS(
                axis * outer,
                rotation,
                new Vector3(SceneViewGizmoGeometry.ConeRadius, SceneViewGizmoGeometry.ConeHeight, SceneViewGizmoGeometry.ConeRadius));
        }

        public virtual Camera ResolveCamera()
        {
            if (IsSceneCamera(TargetCamera))
                return TargetCamera;

            Camera attached = GetComponent<Camera>();
            if (IsSceneCamera(attached))
                return attached;

            if (IsSceneCamera(Camera.main))
                return Camera.main;

            return null;
        }

        public virtual bool IsSceneCamera(Camera camera)
        {
            return camera != null && camera != _gizmoCamera && camera.enabled;
        }

        public virtual Vector3 PivotPoint(Camera camera)
        {
            if (Pivot != null)
                return Pivot.position;
            return camera.transform.position + camera.transform.forward * Mathf.Max(0.01f, OrbitDistance);
        }

        public virtual Quaternion RotationLookingAlong(Transform cameraTransform, Vector3 forward)
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

        public virtual void Remember(Camera camera)
        {
            _previousPose = Capture(camera);
            _hasPrevious = true;
        }

        public virtual CameraPose Capture(Camera camera)
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

        public virtual void ApplyPose(Camera camera, CameraPose pose)
        {
            camera.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            camera.orthographic = pose.Orthographic;
            camera.orthographicSize = pose.OrthographicSize;
            camera.fieldOfView = pose.FieldOfView;
        }

        public virtual Material CreateMaterial()
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

        public virtual float Smooth01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
