using UnityEngine;

public abstract class DebuggableObject : MonoBehaviour
{
    [Header("Debug")]
    [SerializeField] bool enableDebugRays = false;
    [SerializeField] float debugLineScale = 0.1f;

    protected void DrawDebugRay(Vector3 origin, Vector3 ray, Color color, float? customLineScale = null)
    {
        if (!enableDebugRays)
        {
            return;
        }

        float lineScale = customLineScale ?? debugLineScale;

        Debug.DrawRay(origin, ray * lineScale, color, Time.fixedDeltaTime, false);
    }
}
