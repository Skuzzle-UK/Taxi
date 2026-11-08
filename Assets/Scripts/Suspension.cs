using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class Suspension : DebuggableObject
{
    private Rigidbody rb;
    private PlayerControls controls;
    private float accelRate = 0;
    private float steerRate = 0;
    private bool inReverse = false;

    [Header("Suspension")]
    [SerializeField] Transform[] suspensionFixedPoint;
    [SerializeField] LayerMask groundMask = ~0;
    [SerializeField] float restLength = 0.5f;
    [SerializeField] float stiffness = 24500f;
    [SerializeField] float damping = 2500f;

    [Header("Steering")]
    [SerializeField] Transform[] steeringFixedPoint;
    [SerializeField] float maxSteerAngle = 30f;

    [Header("Acceleration")]
    [SerializeField] float acceleration = 10f;
    [SerializeField] float braking = 20f;
    [SerializeField] float maxSpeed = 80f;
    [SerializeField] float maxReverseSpeed = 20f;
    [SerializeField] float rollingResistance = 0.3f;
    [SerializeField] float engineBraking = 2f;

    [Header("Grip")]
    [SerializeField] float frontSideGrip = 10f;
    [SerializeField] float rearSideGrip = 10f;
    [SerializeField] float maxFrontSideAccel = 12f;
    [SerializeField] float maxRearSideAccel = 10f;
    [Range(0f, 1f)]
    [SerializeField] float throttleGripLoss = 0.1f;

    void Awake()
    {
        controls = new();
        rb = GetComponent<Rigidbody>();

        controls.Car.Accel.Enable();
        controls.Car.Decel.Enable();
        controls.Car.Steer.Enable();
    }

    private void Update()
    {
        steerRate = SetSteerRate();
        accelRate = SetAccelRate();
    }

    private float SetSteerRate()
    {
        var stickAngle = controls.Car.Steer.ReadValue<Vector2>();
        return stickAngle.x;
    }

    private float SetAccelRate()
    {
        var accel = controls.Car.Accel.ReadValue<float>();
        var decel = controls.Car.Decel.ReadValue<float>();
        return accel - decel;
    }

    void FixedUpdate()
    {
        UpdateGear(accelRate);

        foreach (Transform point in steeringFixedPoint)
        {
            ApplySteering(point, steerRate);
        }

        foreach (Transform point in suspensionFixedPoint)
        {
            // TODO: add some air resistance to slow down the car when in the air
            // TODO: add some suspension travel to allow the wheel to move up and down when in the air
            // TODO: remove grip when tyre is in the air
            // wheel is in the air, so no forces added
            if (!Physics.Raycast(point.position, -point.up, out RaycastHit hit, restLength, groundMask))
            {
                continue;
            }

            ApplySpring(point, hit);
            ApplyAccel(point, hit, accelRate);
            ApplyGrip(point, hit, accelRate);
        }
    }

    private void UpdateGear(float value)
    {
        // Reverse needed to allow braking to a stop rather than braking past a stop
        if (value > 0f)
        {
            inReverse = false;
        }
        else if (value < 0f && rb.linearVelocity.magnitude < 0.5f)
        {
            inReverse = true;
        }
    }

    private void ApplyAccel(Transform point, RaycastHit hit, float value)
    {
        Debug.Log($"Accel: {value}");

        Vector3 forward = Vector3.ProjectOnPlane(point.forward, hit.normal).normalized;
        Vector3 velocity = rb.GetPointVelocity(point.position);
        float speed = Vector3.Dot(forward, velocity);

        float targetAccel;
        if (value < 0f && !inReverse)
        {
            // Brakes against the direction of travel, without overshooting past a standstill
            float brake = Mathf.Min(-value * braking, Mathf.Abs(speed) / Time.fixedDeltaTime);
            targetAccel = -Mathf.Sign(speed) * brake;
        }
        else
        {
            // Accelerates in the direction of travel, but slows down as it approaches the max speed
            float limit = value >= 0f ? maxSpeed : maxReverseSpeed;
            float power = 1f - Mathf.Clamp01(Mathf.Abs(speed) / limit);
            targetAccel = value * acceleration * power;
        }

        float massPerWheel = rb.mass / suspensionFixedPoint.Length;
        rb.AddForceAtPosition(forward * targetAccel * massPerWheel, point.position);

        // Slows the car when coasting
        float coast = rollingResistance + engineBraking * (1f - Mathf.Abs(value));
        float coastAccel = -Mathf.Sign(speed) * Mathf.Min(coast, Mathf.Abs(speed) / Time.fixedDeltaTime);
        rb.AddForceAtPosition(forward * coastAccel * massPerWheel, point.position);

        DrawDebugRay(hit.point, forward * targetAccel, Color.green);
    }

    private void ApplyGrip(Transform point, RaycastHit hit, float value)
    {
        Vector3 velocity = rb.GetPointVelocity(point.position);
        float massPerWheel = rb.mass / suspensionFixedPoint.Length;

        bool isFront = System.Array.IndexOf(steeringFixedPoint, point) >= 0;
        float grip = isFront ? frontSideGrip : rearSideGrip;
        float maxSideAccel = isFront ? maxFrontSideAccel : maxRearSideAccel;

        if (!isFront)
        {
            maxSideAccel *= 1f - throttleGripLoss * Mathf.Max(0f, value);
        }

        Vector3 right = Vector3.ProjectOnPlane(point.right, hit.normal).normalized;
        float sideSpeed = Vector3.Dot(right, velocity);
        float sideAccel = Mathf.Clamp(-sideSpeed * grip, -maxSideAccel, maxSideAccel);
        rb.AddForceAtPosition(right * sideAccel * massPerWheel, point.position);

        DrawDebugRay(hit.point, right * sideAccel, Color.red);
    }

    private void ApplySteering(Transform point, float value)
    {
        // Reduces the steering angle at higher speeds to prevent twitchy handling
        float speedFactor = Mathf.Lerp(1f, 0.4f, rb.linearVelocity.magnitude / maxSpeed);
        Quaternion steer = Quaternion.Euler(0f, value * maxSteerAngle * speedFactor, 0f);
        point.localRotation = steer;

        DrawDebugRay(point.position, point.forward * 2f, Color.yellow);
    }

    void ApplySpring(Transform point, RaycastHit hit)
    {
        Vector3 origin = point.position;
        Vector3 springDir = point.up;

        float compression = restLength - hit.distance;
        float springVelocity = Vector3.Dot(springDir, rb.GetPointVelocity(origin));
        float force = (stiffness * compression) - (damping * springVelocity);

        force = Mathf.Max(0f, force);

        rb.AddForceAtPosition(springDir * force, origin);

        DrawDebugRay(origin, -springDir * hit.distance, Color.blue, 1);
    }
}