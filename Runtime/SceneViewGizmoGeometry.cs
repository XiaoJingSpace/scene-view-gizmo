using System.Collections.Generic;
using UnityEngine;

namespace SceneViewGizmoKit
{
    public static class SceneViewGizmoGeometry
    {
        public const float CubeSize = 0.42f;
        public const float ConeHeight = 0.69f;
        public const float ConeRadius = 0.21f;
        public const float Gap = 0.008f;
        public const float FitPadding = 1.38f;
        public const float GizmoFov = 28f;
        public const float FadeOutDot = 0.92f;
        public const float FadeInDot = 0.86f;
        public const float FadeDuration = 0.2f;

        public static float Reach
        {
            get { return CubeSize * 0.5f + Gap + ConeHeight; }
        }

        static Mesh _cube;
        static Mesh _cone;

        public static Mesh Cube
        {
            get { return _cube != null ? _cube : (_cube = CreateCube()); }
        }

        public static Mesh Cone
        {
            get { return _cone != null ? _cone : (_cone = CreateCone()); }
        }

        public static void Warmup()
        {
            Mesh cube = Cube;
            Mesh cone = Cone;
            if (cube == null || cone == null)
                Debug.LogError("Scene View Gizmo 网格创建失败。");
        }

        public static bool Raycast(Ray worldRay, Matrix4x4 matrix, bool cone, out float worldDistance)
        {
            Matrix4x4 inverse = matrix.inverse;
            Vector3 direction = worldRay.direction.sqrMagnitude > 1e-8f
                ? worldRay.direction.normalized
                : Vector3.forward;
            Vector3 localOrigin = inverse.MultiplyPoint(worldRay.origin);
            Vector3 localDirection = inverse.MultiplyVector(direction);
            Ray localRay = new Ray(localOrigin, localDirection);

            float distance;
            bool hit = cone
                ? RaycastCone(localRay, out distance)
                : RaycastBox(localRay, new Bounds(Vector3.zero, Vector3.one), out distance);
            if (!hit)
            {
                worldDistance = 0f;
                return false;
            }

            Vector3 worldPoint = matrix.MultiplyPoint(localOrigin + localDirection * distance);
            worldDistance = (worldPoint - worldRay.origin).magnitude;
            return true;
        }

        static Mesh CreateCube()
        {
            var vertices = new List<Vector3>(24);
            var normals = new List<Vector3>(24);
            var colors = new List<Color>(24);
            var triangles = new List<int>(36);
            Vector3[] faces =
            {
                Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
            };

            for (int i = 0; i < faces.Length; i++)
                AddFace(vertices, normals, colors, triangles, faces[i]);

            return CreateMesh("Scene View Gizmo Cube", vertices, normals, colors, triangles);
        }

        static Mesh CreateCone(int segments = 32)
        {
            var vertices = new List<Vector3>(segments * 6);
            var normals = new List<Vector3>(segments * 6);
            var colors = new List<Color>(segments * 6);
            var triangles = new List<int>(segments * 6);
            Vector3 tip = Vector3.up;

            for (int i = 0; i < segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / segments;
                float a1 = (i + 1) * Mathf.PI * 2f / segments;
                Vector3 b0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
                Vector3 b1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                Vector3 n0 = new Vector3(b0.x, 1f, b0.z).normalized;
                Vector3 n1 = new Vector3(b1.x, 1f, b1.z).normalized;
                Vector3 outward = (n0 + n1).normalized;
                AddTriangle(vertices, normals, colors, triangles, b0, tip, b1, n0, outward, n1, outward);
                AddTriangle(vertices, normals, colors, triangles, Vector3.zero, b0, b1, Vector3.down, Vector3.down, Vector3.down, Vector3.down);
            }

            return CreateMesh("Scene View Gizmo Cone", vertices, normals, colors, triangles);
        }

        static void AddFace(List<Vector3> vertices, List<Vector3> normals, List<Color> colors, List<int> triangles, Vector3 normal)
        {
            Vector3 tangent = Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up;
            Vector3 right = Vector3.Cross(tangent, normal).normalized;
            Vector3 up = Vector3.Cross(normal, right).normalized;
            Vector3 center = normal * 0.5f;
            Vector3 a = center - right * 0.5f - up * 0.5f;
            Vector3 b = center + right * 0.5f - up * 0.5f;
            Vector3 c = center + right * 0.5f + up * 0.5f;
            Vector3 d = center - right * 0.5f + up * 0.5f;
            AddTriangle(vertices, normals, colors, triangles, a, b, c, normal, normal, normal, normal);
            AddTriangle(vertices, normals, colors, triangles, a, c, d, normal, normal, normal, normal);
        }

        static void AddTriangle(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Color> colors,
            List<int> triangles,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 na,
            Vector3 nb,
            Vector3 nc,
            Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f)
            {
                Vector3 swap = b;
                b = c;
                c = swap;
                swap = nb;
                nb = nc;
                nc = swap;
            }

            int index = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            normals.Add(na);
            normals.Add(nb);
            normals.Add(nc);
            colors.Add(Color.white);
            colors.Add(Color.white);
            colors.Add(Color.white);
            triangles.Add(index);
            triangles.Add(index + 1);
            triangles.Add(index + 2);
        }

        static Mesh CreateMesh(string name, List<Vector3> vertices, List<Vector3> normals, List<Color> colors, List<int> triangles)
        {
            var mesh = new Mesh();
            mesh.name = name;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            mesh.hideFlags = HideFlags.HideAndDontSave;
            return mesh;
        }

        static bool RaycastBox(Ray ray, Bounds bounds, out float distance)
        {
            Vector3 origin = ray.origin;
            Vector3 direction = ray.direction;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            float tMin = 0f;
            float tMax = float.PositiveInfinity;

            for (int axis = 0; axis < 3; axis++)
            {
                float component = direction[axis];
                float start = origin[axis];
                if (Mathf.Abs(component) < 1e-8f)
                {
                    if (start < min[axis] || start > max[axis])
                    {
                        distance = 0f;
                        return false;
                    }
                    continue;
                }

                float t0 = (min[axis] - start) / component;
                float t1 = (max[axis] - start) / component;
                if (t0 > t1)
                {
                    float swap = t0;
                    t0 = t1;
                    t1 = swap;
                }

                if (t0 > tMin)
                    tMin = t0;
                if (t1 < tMax)
                    tMax = t1;
                if (tMin > tMax)
                {
                    distance = 0f;
                    return false;
                }
            }

            distance = tMin >= 0f ? tMin : tMax;
            return distance >= 0f;
        }

        static bool RaycastCone(Ray ray, out float distance)
        {
            Vector3 origin = ray.origin;
            Vector3 direction = ray.direction;
            float best = float.PositiveInfinity;
            float a = direction.x * direction.x + direction.z * direction.z - direction.y * direction.y;
            float b = 2f * (origin.x * direction.x + origin.z * direction.z + (1f - origin.y) * direction.y);
            float c = origin.x * origin.x + origin.z * origin.z - (1f - origin.y) * (1f - origin.y);

            if (Mathf.Abs(a) > 1e-6f)
            {
                float discriminant = b * b - 4f * a * c;
                if (discriminant >= 0f)
                {
                    float root = Mathf.Sqrt(discriminant);
                    ConsiderConeSide(origin, direction, (-b - root) / (2f * a), ref best);
                    ConsiderConeSide(origin, direction, (-b + root) / (2f * a), ref best);
                }
            }
            else if (Mathf.Abs(b) > 1e-6f)
            {
                ConsiderConeSide(origin, direction, -c / b, ref best);
            }

            if (Mathf.Abs(direction.y) > 1e-6f)
            {
                float cap = -origin.y / direction.y;
                if (cap > 0f)
                {
                    Vector3 point = origin + direction * cap;
                    if (point.x * point.x + point.z * point.z <= 1f && cap < best)
                        best = cap;
                }
            }

            distance = best;
            return best < float.PositiveInfinity;
        }

        static void ConsiderConeSide(Vector3 origin, Vector3 direction, float t, ref float best)
        {
            if (t <= 0f || t >= best)
                return;

            float y = origin.y + direction.y * t;
            if (y >= 0f && y <= 1f)
                best = t;
        }
    }
}
