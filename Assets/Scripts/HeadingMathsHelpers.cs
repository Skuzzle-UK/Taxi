using UnityEngine;

/// <summary>
/// Helpers for working with headings.
/// 0° is +Z (world forward) and 90° is +X (right).
/// </summary>
public static class HeadingMathsHelpers
{
    /// <summary>
    /// Converts a direction vector into a heading, ignoring up/down.
    /// </summary>
    public static float FromDirection(Vector3 direction, float fallback)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
        {
            return fallback;
        }
        return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// Converts a stick (or any 2D input) direction into a heading offset.
    /// </summary>
    public static float From2DInput(Vector2 input)
    {
        return Mathf.Atan2(input.x, input.y) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// Angle from one heading to another. -180 to 180.
    /// </summary>
    public static float AngleBetween(float from, float to)
    {
        return Mathf.DeltaAngle(from, to);
    }

    /// <summary>
    /// Angle to turn to reach a heading.
    /// </summary>
    public static float AngleToTurn(float from, float to, float currentTurnSpeed)
    {
        float delta = AngleBetween(from, to);
        if (Mathf.Abs(delta) > 150f && Mathf.Abs(currentTurnSpeed) > 1f && Mathf.Sign(delta) != Mathf.Sign(currentTurnSpeed))
        {
            delta += 360f * Mathf.Sign(currentTurnSpeed);
        }
        return delta;
    }
}
