using Assets.Scripts.RoadScripts;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;
using System;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Splines;
#endif

[ExecuteInEditMode]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class JunctionBuilder : MonoBehaviour
{
    Mesh m_Mesh;
    readonly List<Vector3> m_Verts = new();
    readonly List<Vector2> m_Uvs = new();
    readonly List<int> m_Tris = new();
    readonly List<Vector3> m_Normals = new();

    const float EdgeOverlap = 0.05f;

    public RoadMeshBehaviour Road;

    public float KerbRadius = 3f;

    public float TileSize = 5f;

    public int KerbSegments = 8;

    struct RoadEnd
    {
        public int spline;
        public float3 point;
        public float3 outDir;
        public float3 left, right;
        public float halfWidth;
        public float angle;
    }

    class Junction
    {
        public float3 centre;
        public List<SplineKnotIndex> endKnots = new();
        public List<SplineKnotIndex> throughKnots = new();
    }

    [ContextMenu("Rebuild")]
    void Rebuild()
    {
        if (Road == null || Road.Container == null)
        {
            return;
        }

        var junctions = FindJunctions();
        ComputeTrims(junctions);
        Road.LoftAllRoads();
        BuildMesh(junctions);
    }

    [NonSerialized]
    bool m_RebuildQueued;

    void OnEnable()
    {
        m_RebuildQueued = false;
        Rebuild();

        Spline.Changed += OnSplineChanged;
#if UNITY_EDITOR
        EditorSplineUtility.RegisterSplineDataChanged<float>(OnSplineDataChanged);
        Undo.undoRedoPerformed += QueueRebuild;
#endif
    }

    void OnDisable()
    {
        Spline.Changed -= OnSplineChanged;
#if UNITY_EDITOR
        EditorSplineUtility.UnregisterSplineDataChanged<float>(OnSplineDataChanged);
        Undo.undoRedoPerformed -= QueueRebuild;
#endif

        if (m_Mesh != null)
        {
#if UNITY_EDITOR
            DestroyImmediate(m_Mesh);
#else
        Destroy(m_Mesh);
#endif
            m_Mesh = null;
        }
    }
    void OnValidate() => QueueRebuild();

    void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification)
    {
        if (Road == null || Road.Container == null) return;

        foreach (var s in Road.Container.Splines)
        {
            if (s == spline)
            {
                QueueRebuild();
                return;
            }
        }
    }

    void OnSplineDataChanged(SplineData<float> data)
    {
        if (Road != null && Road.Widths.Contains(data))
        {
            QueueRebuild();
        }
    }

    void QueueRebuild()
    {
#if UNITY_EDITOR
        if (m_RebuildQueued) return;
        m_RebuildQueued = true;

        EditorApplication.delayCall += () =>
        {
            m_RebuildQueued = false;
            if (this != null)
            {
                Rebuild();
            }
        };
#else
    Rebuild();
#endif
    }

    List<Junction> FindJunctions()
    {
        var result = new List<Junction>();
        var container = Road.Container;
        var splines = container.Splines;
        var links = container.KnotLinkCollection;
        var visited = new HashSet<SplineKnotIndex>();

        for (int s = 0; s < splines.Count; s++)
        {
            for (int k = 0; k < splines[s].Count; k++)
            {
                var idx = new SplineKnotIndex(s, k);

                if (!visited.Add(idx))
                {
                    continue;
                }

                var group = links.GetKnotLinks(idx);
                foreach (var g in group)
                {
                    visited.Add(g);
                }

                if (group.Count < 2)
                {
                    continue;
                }

                var junction = new Junction
                {
                    centre = splines[s][k].Position
                };

                foreach (var g in group)
                {
                    var sp = splines[g.Spline];
                    bool isEnd = !sp.Closed && (g.Knot == 0 || g.Knot == sp.Count - 1);

                    if (isEnd)
                    {
                        junction.endKnots.Add(g);
                    }
                    else
                    {
                        junction.throughKnots.Add(g);
                    }
                }

                int arms = junction.endKnots.Count + junction.throughKnots.Count * 2;
                if (arms >= 3)
                {
                    result.Add(junction);
                }
            }
        }

        return result;
    }

    void ComputeTrims(List<Junction> junctions)
    {
        var splines = Road.Container.Splines;

        Road.Trims.Clear();
        for (int i = 0; i < splines.Count; i++)
        {
            Road.Trims.Add(Vector2.zero);
        }

        foreach (var junction in junctions)
        {
            var ends = new List<RoadEnd>();
            foreach (var knot in junction.endKnots)
            {
                ends.Add(MakeEnd(knot.Spline, knot.Knot == 0, 0f));
            }

            for (int i = 0; i < ends.Count; i++)
            {
                float trim = junction.throughKnots.Count > 0
                    ? StemTrim(ends[i], junction)
                    : EndTrim(ends, i);

                SetTrim(junction.endKnots[i], trim);
            }
        }
    }

    float EndTrim(List<RoadEnd> ends, int i)
    {
        float need = 0f;
        for (int j = 0; j < ends.Count; j++)
        {
            if (j == i)
            {
                continue;
            }

            float cos = math.clamp(math.dot(ends[i].outDir, ends[j].outDir), -1f, 1f);
            if (math.degrees(math.acos(cos)) > 150f)
            {
                continue;
            }

            float sin = math.sqrt(1f - cos * cos);
            float n = (ends[j].halfWidth + ends[i].halfWidth * math.abs(cos)) / math.max(sin, 0.3f);
            need = math.max(need, n);
        }
        return need + KerbRadius;
    }

    float StemTrim(RoadEnd stem, Junction junction)
    {
        float need = 0f;
        foreach (var through in junction.throughKnots)
        {
            var sp = Road.Container.Splines[through.Spline];
            float t = sp.ConvertIndexUnit(through.Knot, PathIndexUnit.Knot, PathIndexUnit.Normalized);
            sp.Evaluate(t, out float3 pos, out float3 tan, out _);

            float3 dir = SafeDirection(sp, t, pos, tan, 1f);
            float cos = math.abs(math.dot(stem.outDir, dir));
            float sin = math.sqrt(1f - math.min(cos * cos, 1f));
            float hw = Road.GetHalfWidth(through.Spline, t);

            float n = (hw + stem.halfWidth * cos) / math.max(sin, 0.3f);
            need = math.max(need, n);
        }
        return need + KerbRadius;
    }

    void SetTrim(SplineKnotIndex knot, float trim)
    {
        var sp = Road.Container.Splines[knot.Spline];
        float maxTrim = math.max(0f, sp.GetLength() * 0.5f - 0.05f);
        trim = math.clamp(trim, 0f, maxTrim);

        var current = Road.Trims[knot.Spline];
        if (knot.Knot == 0)
        {
            current.x = trim;
        }
        else
        {
            current.y = trim;
        }

        Road.Trims[knot.Spline] = current;
    }
    void BuildMesh(List<Junction> junctions)
    {
        m_Verts.Clear();
        m_Uvs.Clear();
        m_Tris.Clear();
        m_Normals.Clear();

        foreach (var j in junctions)
        {
            if (j.throughKnots.Count > 0) BuildStemAprons(j);
            else BuildFill(j);
        }

        if (m_Mesh == null)
        {
            m_Mesh = new Mesh { name = "Junctions", hideFlags = HideFlags.DontSave };
        }

        m_Mesh.Clear();
        m_Mesh.SetVertices(m_Verts);
        m_Mesh.SetUVs(0, m_Uvs);
        m_Mesh.SetTriangles(m_Tris, 0);
        m_Mesh.SetNormals(m_Normals);
        m_Mesh.RecalculateBounds();
        GetComponent<MeshFilter>().sharedMesh = m_Mesh;
        GetComponent<MeshCollider>().sharedMesh = m_Mesh;
    }
    void BuildFill(Junction j)
    {
        var ends = new List<RoadEnd>();
        foreach (var knot in j.endKnots)
        {
            ends.Add(FinalEnd(knot));
        }

        ends.Sort((a, b) => a.angle.CompareTo(b.angle));

        var outline = new List<Vector3>();
        for (int i = 0; i < ends.Count; i++)
        {
            var a = ends[i];
            var b = ends[(i + 1) % ends.Count];

            outline.Add(a.left);
            outline.Add(a.right);

            float gap = b.angle - a.angle;
            if (gap <= 0f)
            {
                gap += 2f * math.PI;
            }

            if (math.degrees(gap) >= 170f)
            {
                continue;
            }

            AddKerb(outline, a.right, a.outDir, b.left, b.outDir);
        }

        AddFan(outline, j.centre);
    }

    void BuildStemAprons(Junction j)
    {
        var through = j.throughKnots[0];
        int ti = through.Spline;
        var sp = Road.Container.Splines[ti];
        float len = sp.GetLength();
        float dK = sp.ConvertIndexUnit(through.Knot, PathIndexUnit.Knot, PathIndexUnit.Distance);

        float tK = sp.ConvertIndexUnit(dK, PathIndexUnit.Distance, PathIndexUnit.Normalized);
        sp.Evaluate(tK, out float3 posT, out float3 tanT, out float3 upT);
        float3 dirT = SafeDirection(sp, tK, posT, tanT, 1f);
        float3 sideT = math.normalizesafe(math.cross(upT, dirT));

        foreach (var knot in j.endKnots)
        {
            var stem = FinalEnd(knot);

            float sideSign = math.sign(math.dot(stem.outDir, sideT));
            if (sideSign == 0f)
            {
                sideSign = 1f;
            }

            float dL = CornerDistance(stem.left, stem.point, posT, dirT, dK, len, out float aL);
            float dR = CornerDistance(stem.right, stem.point, posT, dirT, dK, len, out float aR);
            float3 pL = ThroughEdge(sp, ti, dL, sideSign, out float3 edgeDirL);
            float3 pR = ThroughEdge(sp, ti, dR, sideSign, out float3 edgeDirR);

            var outline = new List<Vector3>();

            outline.Add(stem.left);
            AddKerb(outline, stem.left, stem.outDir, pL, edgeDirL * aL);
            outline.Add(pL);

            for (int i = 1; i < KerbSegments; i++)
            {
                float d = math.lerp(dL, dR, (float)i / KerbSegments);
                outline.Add(ThroughEdge(sp, ti, d, sideSign, out _));
            }

            outline.Add(pR);
            AddKerb(outline, pR, edgeDirR * aR, stem.right, stem.outDir);
            outline.Add(stem.right);

            float dC = math.clamp(dK + math.dot(stem.point - posT, dirT), 0f, len);
            float3 edgeC = ThroughEdge(sp, ti, dC, sideSign, out _);
            AddFan(outline, (stem.point + edgeC) * 0.5f);
        }
    }

    float CornerDistance(float3 q, float3 stemCentre, float3 posT, float3 dirT, float dK, float len, out float a)
    {
        a = math.sign(math.dot(q - stemCentre, dirT));
        if (a == 0f)
        {
            a = 1f;
        }
        float along = math.dot(q - posT, dirT);
        return math.clamp(dK + along + a * KerbRadius, 0f, len);
    }

    float3 ThroughEdge(Spline sp, int splineIndex, float distance, float sideSign, out float3 dir)
    {
        float t = sp.ConvertIndexUnit(distance, PathIndexUnit.Distance, PathIndexUnit.Normalized);
        sp.Evaluate(t, out float3 pos, out float3 tan, out float3 up);
        dir = SafeDirection(sp, t, pos, tan, t < 0.99f ? 1f : -1f);
        float3 side = math.normalizesafe(math.cross(up, dir));
        float hw = Road.GetHalfWidth(splineIndex, t) - EdgeOverlap;
        return pos + hw * sideSign * side;
    }

    RoadEnd MakeEnd(int s, bool atStart, float trimMetres)
    {
        var sp = Road.Container.Splines[s];
        float len = sp.GetLength();
        float t = sp.ConvertIndexUnit(atStart ? trimMetres : len - trimMetres,
                                      PathIndexUnit.Distance, PathIndexUnit.Normalized);
        sp.Evaluate(t, out float3 pos, out float3 tan, out float3 up);

        float3 along = SafeDirection(sp, t, pos, tan, atStart ? 1f : -1f);
        float3 outDir = atStart ? along : -along;
        float3 side = math.normalizesafe(math.cross(up, outDir));
        float hw = Road.GetHalfWidth(s, t);

        return new RoadEnd
        {
            spline = s,
            point = pos,
            outDir = outDir,
            halfWidth = hw,
            left = pos - side * hw,
            right = pos + side * hw,
            angle = math.atan2(outDir.x, outDir.z)
        };
    }
    RoadEnd FinalEnd(SplineKnotIndex knot)
    {
        bool atStart = knot.Knot == 0;
        var trims = Road.Trims[knot.Spline];
        return MakeEnd(knot.Spline, atStart, atStart ? trims.x : trims.y);
    }

    static float3 SafeDirection(Spline sp, float t, float3 pos, float3 tan, float probeSign)
    {
        if (math.lengthsq(tan) > 1e-8f)
        {
            return math.normalize(tan);
        }

        var probe = sp.GetPointAtLinearDistance(t, 0.05f * probeSign, out _);
        var dir = math.normalizesafe(probe - pos) * probeSign;
        return math.lengthsq(dir) > 0f ? dir : new float3(0, 0, 1);
    }

    void AddKerb(List<Vector3> outline, float3 p0, float3 dir0, float3 p3, float3 dir3)
    {
        float h = math.min(0.5523f * KerbRadius, 0.45f * math.distance(p0, p3));
        float3 p1 = p0 - dir0 * h;
        float3 p2 = p3 - dir3 * h;

        for (int s = 1; s < KerbSegments; s++)
        {
            outline.Add(Bezier(p0, p1, p2, p3, (float)s / KerbSegments));
        }
    }

    static float3 Bezier(float3 a, float3 b, float3 c, float3 d, float u)
    {
        float v = 1f - u;
        return v * v * v * a + 3f * v * v * u * b + 3f * v * u * u * c + u * u * u * d;
    }

    const int FanRings = 4;

    void AddFan(List<Vector3> outline, Vector3 centre)
    {
        if (outline.Count < 3)
        {
            return;
        }

        float area = 0f;
        for (int i = 0; i < outline.Count; i++)
        {
            var p = outline[i];
            var q = outline[(i + 1) % outline.Count];
            area += (q.x - p.x) * (q.z + p.z);
        }
        if (area < 0f) outline.Reverse();

        FitPlane(outline, out float mx, out float my, out float mz, out float a, out float b);
        float PlaneY(Vector3 p) => my + a * (p.x - mx) + b * (p.z - mz);

        centre.y = PlaneY(centre);

        int n = outline.Count;
        int start = m_Verts.Count;
        AddVertex(centre);

        for (int r = 1; r <= FanRings; r++)
        {
            float f = (float)r / FanRings;
            foreach (var o in outline)
            {
                var p = Vector3.Lerp(centre, o, f);
                p.y = PlaneY(p) + f * f * (o.y - PlaneY(o));
                AddVertex(p);
            }
        }

        int Ring(int r, int i) => start + 1 + (r - 1) * n + (i % n);

        for (int i = 0; i < n; i++)
        {
            m_Tris.Add(start);
            m_Tris.Add(Ring(1, i));
            m_Tris.Add(Ring(1, i + 1));
        }

        for (int r = 2; r <= FanRings; r++)
        {
            for (int i = 0; i < n; i++)
            {
                m_Tris.Add(Ring(r - 1, i));
                m_Tris.Add(Ring(r, i));
                m_Tris.Add(Ring(r, i + 1));

                m_Tris.Add(Ring(r - 1, i));
                m_Tris.Add(Ring(r, i + 1));
                m_Tris.Add(Ring(r - 1, i + 1));
            }
        }
    }

    void AddVertex(Vector3 p)
    {
        m_Verts.Add(p);
        m_Normals.Add(Vector3.up);
        m_Uvs.Add(new Vector2(p.x, p.z) / TileSize);
    }

    static void FitPlane(List<Vector3> pts, out float mx, out float my, out float mz,
                         out float a, out float b)
    {
        mx = my = mz = 0f;
        foreach (var p in pts)
        {
            mx += p.x;
            my += p.y;
            mz += p.z;
        }
        mx /= pts.Count;
        my /= pts.Count;
        mz /= pts.Count;

        float sxx = 0f, sxz = 0f, szz = 0f, sxy = 0f, szy = 0f;
        foreach (var p in pts)
        {
            float dx = p.x - mx, dz = p.z - mz, dy = p.y - my;
            sxx += dx * dx; sxz += dx * dz; szz += dz * dz;
            sxy += dx * dy; szy += dz * dy;
        }

        float det = sxx * szz - sxz * sxz;
        if (math.abs(det) < 1e-6f)
        {
            a = 0f; b = 0f; return;
        }

        a = (sxy * szz - sxz * szy) / det;
        b = (sxx * szy - sxz * sxy) / det;
    }
}
